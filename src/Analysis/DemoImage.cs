using System.Text;

namespace BiosFlashStudio.Analysis;

/// <summary>
/// Builds a synthetic, clearly-fake Intel-style firmware image used by the simulator so that
/// every analysis feature can be demonstrated without real hardware or real customer data.
/// </summary>
public static class DemoImage
{
    public static byte[] Build(int size)
    {
        var d = new byte[size];
        Array.Fill(d, (byte)0xFF);
        if (size < 8 * 1024 * 1024) return d;
        var rnd = new Random(2026);
        void W32(int o, uint v) => BitConverter.GetBytes(v).CopyTo(d, o);
        void W16(int o, ushort v) => BitConverter.GetBytes(v).CopyTo(d, o);
        void Str(int o, string s) => Encoding.ASCII.GetBytes(s).CopyTo(d, o);
        void Wstr(int o, string s) => Encoding.Unicode.GetBytes(s + "\0").CopyTo(d, o);

        int meStart = 0x3000, biosStart = size / 16 * 5;     // 16 MB -> BIOS @ 0x500000
        int meEnd = biosStart - 1, top = size - 1;

        // ---- Intel Flash Descriptor (v2)
        Array.Fill(d, (byte)0x00, 0, 0x1000);
        W32(0x10, 0x0FF0A55A);
        W32(0x14, 0x03u | (0x04u << 16) | (0x04u << 24));        // FCBA=0x30 FRBA=0x40
        W32(0x18, 0x08u | (0x02u << 8));                         // FMBA=0x80
        W32(0x30, 0x5u | (6u << 17));                            // density + 17 MHz read => IFD v2
        W32(0x40, 0x00000000);                                   // descriptor 0x0-0xFFF
        W32(0x44, (uint)((top >> 12) << 16 | (biosStart >> 12)));
        W32(0x48, (uint)((meEnd >> 12) << 16 | (meStart >> 12)));
        W32(0x4C, 0x00020001);                                   // GbE 0x1000-0x2FFF
        for (int i = 4; i < 16; i++) W32(0x40 + i * 4, 0x00007FFF);
        W32(0x80, 0x00A00B00);                                   // host: R desc/bios/gbe, W bios/gbe
        W32(0x84, 0x00C00D00);                                   // ME
        W32(0x88, 0x00800900);                                   // GbE
        Str(0x200, "Synthetic demo descriptor - BiosFlash Studio simulator");

        // ---- GbE (locally-administered demo MAC)
        Array.Fill(d, (byte)0x00, 0x1000, 0x2000);
        foreach (int bank in new[] { 0x1000, 0x2000 })
        {
            new byte[] { 0x02, 0xDE, 0x5A, 0x00, 0x12, 0x34 }.CopyTo(d, bank);
            W16(bank + 0x26, 0x8205);                              // word 0x13: valid NVM signature (bits 15:14 = 10b)
            int sum = 0;
            for (int w = 0; w < 0x3F; w++) sum += BitConverter.ToUInt16(d, bank + w * 2);
            W16(bank + 0x7E, (ushort)((0xBABA - sum) & 0xFFFF));   // word 0x3F: checksum
        }

        // ---- ME region: $FPT + $MN2 manifest
        Array.Fill(d, (byte)0x00, meStart, 0x10);
        int fpt = meStart + 0x10;
        Str(fpt, "$FPT"); W32(fpt + 4, 3); d[fpt + 8] = 0x20; d[fpt + 9] = 0x10; d[fpt + 10] = 0x30;
        W16(fpt + 0x18, 14); W16(fpt + 0x1A, 0); W16(fpt + 0x1C, 39); W16(fpt + 0x1E, 1339);
        void Part(int idx, string name, int off, int len)
        {
            int e = fpt + 0x20 + idx * 0x20;
            Array.Fill(d, (byte)0, e, 0x20);
            Str(e, name); W32(e + 8, (uint)off); W32(e + 12, (uint)len);
        }
        Part(0, "FTPR", 0x1000, 0x80000);
        Part(1, "MFS", 0x81000, 0x60000);
        Part(2, "FTUP", 0xE1000, 0x40000);
        int ftpr = meStart + 0x1000;
        Str(ftpr, "$CPD");
        for (int i = ftpr + 0x40; i < ftpr + 0x80000; i++) d[i] = (byte)rnd.Next(256);
        int man = ftpr + 0x200;
        Array.Fill(d, (byte)0, man, 0x80);
        W32(man, 4); W32(man + 4, 0xA1); W32(man + 8, 0x10000); W32(man + 0xC, 0);
        W32(man + 0x10, 0x8086); W32(man + 0x14, 0x20260115); W32(man + 0x18, 0x400);
        Str(man + 0x1C, "$MN2");
        W16(man + 0x24, 14); W16(man + 0x26, 0); W16(man + 0x28, 39); W16(man + 0x2A, 1339);
        W32(man + 0x2C, 7); W32(man + 0x30, 6);
        for (int i = meStart + 0x81000; i < meStart + 0x90000; i++) d[i] = (byte)rnd.Next(256);

        // ---- BIOS region: NVRAM (VSS)
        int vss = biosStart + 0x20000;
        Str(vss, "$VSS"); W32(vss + 4, 0x10000); d[vss + 8] = 0x5A; d[vss + 9] = 0xFE;
        int p = vss + 16;
        var g = new Guid("8BE4DF61-93CA-11D2-AA0D-00E098032B8C").ToByteArray();
        void Var(string name, byte[] data)
        {
            var nb = Encoding.Unicode.GetBytes(name + "\0");
            d[p] = 0xAA; d[p + 1] = 0x55; d[p + 2] = 0x3F; d[p + 3] = 0;
            W32(p + 4, 7); W32(p + 8, (uint)nb.Length); W32(p + 12, (uint)data.Length);
            g.CopyTo(d, p + 16);
            nb.CopyTo(d, p + 32);
            data.CopyTo(d, p + 32 + nb.Length);
            p += 32 + nb.Length + data.Length;
            p = (p + 3) & ~3;
        }
        Var("BootOrder", new byte[] { 0, 0, 1, 0, 2, 0 });
        Var("Lang", Encoding.ASCII.GetBytes("eng"));
        Var("PlatformLang", Encoding.ASCII.GetBytes("en-US\0"));
        Var("Timeout", new byte[] { 3, 0 });
        Var("Setup", Enumerable.Range(0, 64).Select(i => (byte)(i * 7)).ToArray());

        // ---- SMBIOS default tables
        int sm = biosStart + 0x100000;
        byte[] t0 = { 0, 0x18, 0, 0, 1, 2, 0, 0xF0, 3, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF };
        t0.CopyTo(d, sm); sm += t0.Length;
        foreach (var s in new[] { "American Megatrends International, LLC.", "DEMO.01.05", "06/12/2026" }) { Str(sm, s); sm += s.Length + 1; d[sm - 1] = 0; }
        d[sm++] = 0;
        var t1 = new byte[0x1B];
        t1[0] = 1; t1[1] = 0x1B; t1[4] = 1; t1[5] = 2; t1[6] = 3; t1[7] = 4;
        new Guid("5F0D3B1A-9C2E-4C77-8A11-00DEC0DE2026").ToByteArray().CopyTo(t1, 8);
        t1[0x18] = 6; t1[0x19] = 5; t1[0x1A] = 6;
        t1.CopyTo(d, sm); sm += t1.Length;
        foreach (var s in new[] { "Demo Systems Inc.", "DemoBook 14 G2 (Simulated)", "SKU-DEMO", "SIM0000001", "DEMO-14G2", "DemoBook" }) { Str(sm, s); sm += s.Length + 1; d[sm - 1] = 0; }
        d[sm++] = 0;
        var t2 = new byte[0x0F];
        t2[0] = 2; t2[1] = 0x0F; t2[4] = 1; t2[5] = 2; t2[6] = 3; t2[7] = 4;
        t2.CopyTo(d, sm); sm += t2.Length;
        foreach (var s in new[] { "Demo Systems Inc.", "8A1D", "KBC 01.02", "SIMBOARD00001" }) { Str(sm, s); sm += s.Length + 1; d[sm - 1] = 0; }
        d[sm++] = 0;

        // ---- MSDM ACPI table with an obviously fake demo key
        int msdm = biosStart + 0x180000;
        Array.Fill(d, (byte)0, msdm, 0x55);
        Str(msdm, "MSDM"); W32(msdm + 4, 0x55); d[msdm + 8] = 3;
        Str(msdm + 10, "DEMO  "); Str(msdm + 16, "SIMULATR");
        W32(msdm + 0x24, 1); W32(msdm + 0x2C, 1); W32(msdm + 0x34, 0x1D);
        Str(msdm + 0x38, "DEMO1-SIMUL-ATED0-KEY00-NOTRL");

        // ---- boot logos: an uncompressed BMP and a JPEG inside an LZMA-compressed volume
        try
        {
            var fv1 = Fv(0x10000, new[] { Ffs(Uefi.DefaultLogoGuid, 0x02, 0x40, Ui("Logo"), Section(0x19, DemoBmp())) });
            fv1.CopyTo(d, biosStart + 0x40000);
            var dxe = Ffs(new Guid("D6A2CB7F-6A18-4E2F-B43B-9920A733700A"), 0x07, 0x00, Ui("DemoDxeDriver"), Section(0x10, FakeCode(0x18000)));
            var logoFile = Ffs(new Guid("A59A0056-3341-44B5-9C9C-6D76F7673817"), 0x02, 0x40, Ui("OEMLogo"), Section(0x19, DemoJpeg()));
            var innerFv = Fv(0x40000, new[] { dxe, logoFile });
            var fv2 = Fv(0x80000, new[] { Ffs(new Guid("9E21FD93-9C72-4C15-8C4B-E77F1DB2D792"), 0x0B, 0x00, LzmaSection(Section(0x17, innerFv))) });
            fv2.CopyTo(d, biosStart + 0x60000);
        }
        catch { /* logos are optional demo content */ }

        // ---- version strings + some code-like content
        Str(biosStart + 0x200000, "$IBIOSI$");
        Wstr(biosStart + 0x200008, "DEMOBK14.86A.0105.2026.0612.1200");
        Str(biosStart + 0x200100, "American Megatrends");
        for (int blk = biosStart + 0x300000; blk < top - 0x100000; blk += 0x10000)
        {
            if (rnd.Next(3) == 0) continue;
            int n = rnd.Next(0x4000, 0x10000);
            for (int i = 0; i < n; i++) d[blk + i] = (byte)rnd.Next(256);
        }
        for (int i = top - 0xFFFF; i <= top - 16; i++) d[i] = (byte)rnd.Next(256);
        return d;
    }

    // ================================================================== minimal PI firmware builder (demo + tests)
    static readonly Guid Ffs2Guid = new("8C8CE578-8A3D-4F1C-9935-896185C32DD3");

    public static byte[] Section(byte type, byte[] payload)
    {
        var s = new byte[4 + payload.Length];
        s[0] = (byte)s.Length; s[1] = (byte)(s.Length >> 8); s[2] = (byte)(s.Length >> 16); s[3] = type;
        payload.CopyTo(s, 4);
        return s;
    }

    public static byte[] Ui(string name) => Section(0x15, Encoding.Unicode.GetBytes(name + "\0"));

    public static byte[] LzmaSection(byte[] content)
    {
        var lz = Uefi.LzmaCompress(content, 1 << 20);
        var s = new byte[24 + lz.Length];
        s[0] = (byte)s.Length; s[1] = (byte)(s.Length >> 8); s[2] = (byte)(s.Length >> 16); s[3] = 0x02;
        Uefi.LzmaGuid.ToByteArray().CopyTo(s, 4);
        BitConverter.GetBytes((ushort)24).CopyTo(s, 20);
        BitConverter.GetBytes((ushort)0x01).CopyTo(s, 22);   // EFI_GUIDED_SECTION_PROCESSING_REQUIRED
        lz.CopyTo(s, 24);
        return s;
    }

    public static byte[] Ffs(Guid name, byte type, byte attr, params byte[][] sections)
    {
        var body = new List<byte>();
        foreach (var s in sections)
        {
            while (body.Count % 4 != 0) body.Add(0);
            body.AddRange(s);
        }
        int size = 24 + body.Count;
        var f = new byte[size];
        name.ToByteArray().CopyTo(f, 0);
        f[18] = type; f[19] = attr;
        f[20] = (byte)size; f[21] = (byte)(size >> 8); f[22] = (byte)(size >> 16);
        f[23] = 0xF8;                                          // header + data valid (erase polarity 1)
        body.ToArray().CopyTo(f, 24);
        int dsum = 0;
        if ((attr & 0x40) != 0) { for (int i = 24; i < size; i++) dsum += f[i]; f[17] = (byte)(0x100 - (dsum & 0xFF)); }
        else f[17] = 0xAA;
        int hsum = 0;
        for (int i = 0; i < 24; i++) if (i is not (16 or 17 or 23)) hsum += f[i];
        f[16] = (byte)(0x100 - (hsum & 0xFF));
        return f;
    }

    public static byte[] Fv(int size, IEnumerable<byte[]> files)
    {
        var v = new byte[size];
        Array.Fill(v, (byte)0xFF);
        Array.Clear(v, 0, 0x48);
        Ffs2Guid.ToByteArray().CopyTo(v, 0x10);
        BitConverter.GetBytes((ulong)size).CopyTo(v, 0x20);
        "_FVH"u8.ToArray().CopyTo(v, 0x28);
        BitConverter.GetBytes(0x0004FEFFu).CopyTo(v, 0x2C);
        BitConverter.GetBytes((ushort)0x48).CopyTo(v, 0x30);
        v[0x37] = 2;
        BitConverter.GetBytes((uint)(size / 0x1000)).CopyTo(v, 0x38);
        BitConverter.GetBytes(0x1000u).CopyTo(v, 0x3C);
        int sum = 0;
        for (int i = 0; i < 0x48; i += 2) sum += BitConverter.ToUInt16(v, i);
        BitConverter.GetBytes((ushort)((0x10000 - (sum & 0xFFFF)) & 0xFFFF)).CopyTo(v, 0x32);
        int p = 0x48;
        foreach (var f in files)
        {
            p = (p + 7) & ~7;
            if (p + f.Length > size) throw new ArgumentException("volume too small");
            f.CopyTo(v, p);
            p += f.Length;
        }
        return v;
    }

    static byte[] FakeCode(int n)
    {
        var r = new Random(77);
        var b = new byte[n];
        string[] ops = { "\x48\x89\x5C\x24\x08", "\x57\x48\x83\xEC\x20", "\xE8\x00\x00\x00\x00", "\x48\x8B\xC8", "\xC3", "\x33\xC0", "\x48\x85\xC0\x74\x05" };
        for (int i = 0; i < n;)
        {
            var op = ops[r.Next(ops.Length)];
            for (int k = 0; k < op.Length && i < n; k++) b[i++] = (byte)op[k];
            if (r.Next(9) == 0 && i + 2 < n) { b[i++] = (byte)r.Next(256); b[i++] = (byte)r.Next(256); }
        }
        return b;
    }

    static SkiaSharp.SKBitmap DrawLogo(int w, int h, SkiaSharp.SKColor a, SkiaSharp.SKColor b)
    {
        var bmp = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque);
        using var c = new SkiaSharp.SKCanvas(bmp);
        c.Clear(new SkiaSharp.SKColor(0x0F, 0x13, 0x19));
        using var grad = new SkiaSharp.SKPaint
        {
            IsAntialias = true,
            Shader = SkiaSharp.SKShader.CreateLinearGradient(new SkiaSharp.SKPoint(0, 0), new SkiaSharp.SKPoint(w, h), new[] { a, b }, SkiaSharp.SKShaderTileMode.Clamp),
        };
        float s = h * 0.7f;
        c.DrawRoundRect(SkiaSharp.SKRect.Create(h * 0.15f, h * 0.15f, s, s), s * 0.2f, s * 0.2f, grad);
        using var ink = new SkiaSharp.SKPaint { IsAntialias = true, Color = new SkiaSharp.SKColor(0x04, 0x24, 0x1A), Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, h / 22f) };
        float cx = h * 0.15f + s * 0.32f, cy = h * 0.15f + s * 0.25f;
        c.DrawRoundRect(SkiaSharp.SKRect.Create(cx, cy, s * 0.36f, s * 0.5f), 4, 4, ink);
        for (int i = 0; i < 3; i++)
        {
            float y = cy + s * 0.1f + i * s * 0.15f;
            c.DrawLine(cx - s * 0.1f, y, cx, y, ink);
            c.DrawLine(cx + s * 0.36f, y, cx + s * 0.46f, y, ink);
        }
        using var bar = new SkiaSharp.SKPaint { IsAntialias = true, Shader = grad.Shader };
        for (int i = 0; i < 4; i++)
            c.DrawRoundRect(SkiaSharp.SKRect.Create(h * 1.05f, h * (0.22f + i * 0.16f), (w - h * 1.25f) * (1 - i * 0.18f), h * 0.08f), 3, 3, bar);
        c.Flush();
        return bmp;
    }

    static byte[] DemoBmp()
    {
        using var b = DrawLogo(200, 80, new SkiaSharp.SKColor(0x3D, 0xDC, 0x97), new SkiaSharp.SKColor(0x6C, 0xB4, 0xFF));
        return LogoImaging.EncodeBmp(b, 24);
    }

    static byte[] DemoJpeg()
    {
        using var b = DrawLogo(320, 120, new SkiaSharp.SKColor(0xF7, 0xB9, 0x55), new SkiaSharp.SKColor(0xFF, 0x6B, 0x7A));
        using var img = SkiaSharp.SKImage.FromBitmap(b);
        using var enc = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 88);
        return enc.ToArray();
    }
}
