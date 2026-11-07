using BiosFlashStudio.Analysis;
using SkiaSharp;

/// <summary>EFI 1.1 / Tiano codec and AMI Aptio 4 style logo replacement (logo file in a Tiano-compressed section).</summary>
static class Tests5
{
    static byte[] Bmp24(int w, int h, bool detailed)
    {
        using var b = new SKBitmap(w, h);
        using (var c = new SKCanvas(b))
        {
            c.Clear(SKColors.Black);
            using var p = new SKPaint { Color = SKColors.OrangeRed, IsAntialias = true };
            c.DrawCircle(w / 2f, h / 2f, h / 3f, p);
            if (detailed)
            {
                var r = new Random(9);
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if ((x + y) % 5 == 0) b.SetPixel(x, y, new SKColor((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256)));
            }
        }
        int stride = (w * 3 + 3) & ~3;
        var bmp = new byte[54 + stride * h];
        "BM"u8.ToArray().CopyTo(bmp, 0);
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(w).CopyTo(bmp, 18);
        BitConverter.GetBytes(h).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(stride * h).CopyTo(bmp, 34);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var px = b.GetPixel(x, h - 1 - y);
                int o = 54 + y * stride + x * 3;
                bmp[o] = px.Blue; bmp[o + 1] = px.Green; bmp[o + 2] = px.Red;
            }
        return bmp;
    }

    /// <summary>PI compression section (type 0x01, EFI_STANDARD_COMPRESSION) holding <paramref name="content"/>.</summary>
    static byte[] CompressionSection(byte[] content, EfiCompression.Variant v)
    {
        var stream = EfiCompression.Compress(content, v);
        var s = new byte[9 + stream.Length];
        s[0] = (byte)s.Length; s[1] = (byte)(s.Length >> 8); s[2] = (byte)(s.Length >> 16); s[3] = 0x01;
        BitConverter.GetBytes((uint)content.Length).CopyTo(s, 4);
        s[8] = 0x01;
        stream.CopyTo(s, 9);
        return s;
    }

    public static void Run(Reporter rep)
    {
        rep.Section("EFI / Tiano compression codec");
        var rnd = new Random(3);
        var samples = new List<(string name, byte[] data)>
        {
            ("empty", Array.Empty<byte>()),
            ("1 byte", new byte[] { 0x42 }),
            ("64 KB zeros", new byte[65536]),
            ("text", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("AMI Aptio logo test — the quick brown fox. ", 3000)))),
            ("random 20 KB", Enumerable.Range(0, 20000).Select(_ => (byte)rnd.Next(256)).ToArray()),
            ("BMP 640×360", Bmp24(640, 360, true)),
            ("long runs", Enumerable.Range(0, 300000).Select(i => (byte)(i / 1000 % 3)).ToArray()),
        };
        foreach (var v in new[] { EfiCompression.Variant.Efi, EfiCompression.Variant.Tiano })
            foreach (var (name, data) in samples)
            {
                var c = EfiCompression.Compress(data, v);
                bool ok = EfiCompression.TryDecompress(c, 0, c.Length, v, out var back) && back.AsSpan().SequenceEqual(data);
                rep.Check(ok, $"{v}: {name} round-trips ({data.Length:N0} → {c.Length:N0} bytes)");
            }
        var good = EfiCompression.Compress(samples[5].data, EfiCompression.Variant.Tiano);
        var bad = (byte[])good.Clone();
        for (int i = 20; i < 60; i++) bad[i] ^= 0x5A;
        rep.Check(!EfiCompression.TryDecompress(bad, 0, bad.Length, EfiCompression.Variant.Tiano, out var junk) || !junk.AsSpan().SequenceEqual(samples[5].data), "corrupt stream is rejected (or never decodes to the original)");
        rep.Check(!EfiCompression.TryDecompress(good, 0, good.Length - 10, EfiCompression.Variant.Tiano, out _), "truncated stream is rejected");

        rep.Section("Boot logo: AMI Aptio 4 (Tiano-compressed logo file)");
        var logoBmp = Bmp24(320, 180, false);
        var logoFile = DemoImage.Ffs(Uefi.DefaultLogoGuid, 0x02, 0x40, CompressionSection(DemoImage.Section(0x19, logoBmp), EfiCompression.Variant.Tiano));
        var other = DemoImage.Ffs(new Guid("FD44820B-F1AB-41C0-AE4E-0C55556EB9BD"), 0x02, 0x00, DemoImage.Section(0x19, new byte[300]));
        var img = DemoImage.Fv(0x40000, new[] { other, logoFile });
        var (logos, tree) = BootLogo.Scan(img);
        var logo = logos.FirstOrDefault(l => l.Info.Format == "BMP" && l.Info.Width == 320);
        rep.Check(logo != null && logo.Compressed && logo.Compression == "Tiano" && logo.Replaceable, $"logo found inside the Tiano section: {logo?.Where}");
        rep.Check(logo != null && logo.Bytes.AsSpan().SequenceEqual(logoBmp), "logo extracts byte-exact");

        var simple = (byte[])logoBmp.Clone();
        Array.Clear(simple, 54, simple.Length - 54);   // plain black: compresses far below the original
        var r1 = BootLogo.Replace(img, logo, simple);
        foreach (var c in r1.Checks) rep.Info($"verify: {(c.ok ? "ok " : "BAD")} {c.text}");
        rep.Check(r1.Ok && r1.GrownEnd < 0, "replace in place (fits the original section) " + r1.Error);

        var detailed = Bmp24(320, 180, true);
        var r2 = BootLogo.Replace(img, logo, detailed);
        foreach (var c in r2.Checks) rep.Info($"verify: {(c.ok ? "ok " : "BAD")} {c.text}");
        rep.Check(r2.Ok && r2.GrownEnd > 0, "replace with a larger stream: file grows into the volume's free space " + r2.Error);
        if (r2.Ok)
        {
            var (after, _) = BootLogo.Scan(r2.Image);
            rep.Check(after.Any(l => l.Bytes.AsSpan().SequenceEqual(detailed)), "grown logo decompresses byte-exact from the rebuilt image");
            rep.Check(r2.Image.AsSpan(0, (int)logo.Container.File.Off).SequenceEqual(img.AsSpan(0, (int)logo.Container.File.Off)), "everything before the logo file is unchanged");
        }

        // the same logo file followed by another file: growth must be refused, nothing may move
        var blocked = DemoImage.Fv(0x40000, new[] { logoFile, other });
        var (bl, _) = BootLogo.Scan(blocked);
        var r3 = BootLogo.Replace(blocked, bl.First(l => l.Info.Width == 320), detailed);
        rep.Check(!r3.Ok && r3.Error.Contains("another file follows"), "growth refused when another file follows: " + r3.Error);
    }
}
