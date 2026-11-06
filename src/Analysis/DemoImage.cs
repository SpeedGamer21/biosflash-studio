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
}
