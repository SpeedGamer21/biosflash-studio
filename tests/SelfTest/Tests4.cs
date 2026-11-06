using BiosFlashStudio.Analysis;
using SkiaSharp;

/// <summary>Boot logo engine: UEFI parsing, LZMA rebuild, conversions and safety nets.</summary>
static class Tests4
{
    static byte[] UserPicture(int w, int h, bool noisy = false)
    {
        using var b = new SKBitmap(w, h);
        using (var c = new SKCanvas(b))
        {
            using var p = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), new[] { SKColors.MidnightBlue, SKColors.Orange }, SKShaderTileMode.Clamp) };
            c.DrawRect(0, 0, w, h, p);
            using var dot = new SKPaint { Color = SKColors.White, IsAntialias = true };
            for (int i = 0; i < 6; i++) c.DrawCircle(w * (0.15f + i * 0.14f), h / 2f, h * 0.12f, dot);
            if (noisy)
            {
                var r = new Random(5);
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) b.SetPixel(x, y, new SKColor((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256)));
            }
        }
        using var img = SKImage.FromBitmap(b);
        using var enc = img.Encode(SKEncodedImageFormat.Png, 100);
        return enc.ToArray();
    }

    public static void Run(Reporter rep, string outDir = null)
    {
        rep.Section("Boot logo: UEFI parser and logo detection");
        var demo = DemoImage.Build(16 << 20);
        var tree = Uefi.Parse(demo);
        var files = Uefi.All(tree).Where(n => n.Kind == UefiKind.File).ToList();
        rep.Check(tree.Volumes.Count >= 2 && files.Count >= 3, $"uefi: {tree.Volumes.Count} volumes, {files.Count} files, {tree.Buffers.Count - 1} decompressed buffer(s)");
        rep.Check(files.All(f => Uefi.FileChecksumsOk(f.Buf.Data, f)), "uefi: all FFS checksums valid in the original");
        var (logos, _) = BootLogo.Scan(demo);
        foreach (var l in logos) rep.Info($"logo: {l.Title,-40} {l.Where}  slot {l.Slot:N0}  replaceable={l.Replaceable}");
        var bmp = logos.FirstOrDefault(l => l.Info.Format == "BMP" && !l.Compressed);
        var jpg = logos.FirstOrDefault(l => l.Info.Format == "JPEG" && l.Compressed);
        rep.Check(bmp != null && bmp.Info.Width == 200 && bmp.Info.Height == 80 && bmp.Info.Bpp == 24 && bmp.Replaceable && bmp.Location.Contains("'Logo'"), "logo: uncompressed 24-bit BMP 200×80 in 'Logo'");
        rep.Check(jpg != null && jpg.Info.Width == 320 && jpg.Info.Height == 120 && jpg.Replaceable && jpg.Location.Contains("OEMLogo"), "logo: JPEG 320×120 inside LZMA volume ('OEMLogo')");
        rep.Check(!BootLogo.DetectProtection(demo).BootGuard, "protection: no Boot Guard in demo image");

        if (outDir != null && jpg != null)
        {
            // export one real UEFI LZMA stream + its plain data for an external xz/lzma cross-check
            var sec = jpg.Buffer.Owner;
            File.WriteAllBytes(Path.Combine(outDir, "lzma-sample.lzma"), demo.AsSpan((int)sec.DataOff, (int)(sec.End - sec.DataOff)).ToArray());
            File.WriteAllBytes(Path.Combine(outDir, "lzma-sample.bin"), jpg.Buffer.Data);
        }

        if (outDir != null) File.WriteAllBytes(Path.Combine(outDir, "sample-logo.png"), UserPicture(800, 300));
        rep.Section("Boot logo: replace uncompressed BMP");
        var conv = LogoImaging.Convert(UserPicture(640, 480), bmp.Info, bmp.Bytes, bmp.Slot, LogoFit.Fit);
        foreach (var c in conv.Checks) rep.Info($"convert: {(c.ok ? "ok " : "BAD")} {c.text}");
        rep.Check(conv.Ok && conv.Info.Format == "BMP" && conv.Info.Bpp == 24 && conv.Bytes.Length == bmp.Info.Length, "convert: 640×480 PNG → 200×80 24-bit BMP, same size as original");
        var r1 = BootLogo.Replace(demo, bmp, conv.Bytes);
        foreach (var c in r1.Checks) rep.Info($"verify: {(c.ok ? "ok " : "BAD")} {c.text}");
        rep.Check(r1.Ok, "replace: BMP replaced and verified " + r1.Error);
        if (r1.Ok)
        {
            var a1 = BiosAnalyzer.Analyze(r1.Image);
            rep.Check(a1.HasDescriptor && a1.Me.Version == "14.0.39.1339" && a1.Regions.Count == 4, "replace: rest of firmware unchanged (descriptor, ME)");
        }

        rep.Section("Boot logo: replace JPEG inside LZMA-compressed volume");
        var conv2 = LogoImaging.Convert(UserPicture(1000, 300), jpg.Info, jpg.Bytes, jpg.Slot, LogoFit.Fill);
        foreach (var n in conv2.Notes) rep.Info("note: " + n);
        rep.Check(conv2.Ok && conv2.Info.Format == "JPEG" && !conv2.Info.Progressive && conv2.Bytes.Length <= jpg.Slot, $"convert: → baseline JPEG {conv2.Info?.Width}×{conv2.Info?.Height}, {conv2.Bytes?.Length:N0} of {jpg.Slot:N0} bytes");
        var r2 = BootLogo.Replace(demo, jpg, conv2.Bytes);
        foreach (var c in r2.Checks) rep.Info($"verify: {(c.ok ? "ok " : "BAD")} {c.text}");
        rep.Check(r2.Ok, "replace: compressed JPEG replaced, volume recompressed and verified " + r2.Error);
        if (r2.Ok)
        {
            var (after, _) = BootLogo.Scan(r2.Image);
            var nj = after.FirstOrDefault(l => l.Compressed && l.Info.Format == "JPEG");
            rep.Check(nj != null && nj.Bytes.AsSpan().SequenceEqual(conv2.Bytes), "replace: new JPEG decompresses byte-exact from the rebuilt image");
            if (outDir != null && nj != null)
            {
                var s2 = nj.Buffer.Owner;
                File.WriteAllBytes(Path.Combine(outDir, "lzma-rebuilt.lzma"), r2.Image.AsSpan((int)s2.DataOff, (int)(s2.End - s2.DataOff)).ToArray());
                File.WriteAllBytes(Path.Combine(outDir, "lzma-rebuilt.bin"), nj.Buffer.Data);
            }
            rep.Check(after.Any(l => !l.Compressed && l.Bytes.AsSpan().SequenceEqual(bmp.Bytes)), "replace: the other logo is untouched");
        }

        rep.Section("Boot logo: safety nets");
        var png = UserPicture(320, 120);
        rep.Check(!BootLogo.Replace(demo, jpg, png).Ok, "safety: PNG refused where firmware expects JPEG");
        var wrongSize = LogoImaging.Convert(UserPicture(64, 64), new PictureInfo { Format = "JPEG", Width = 100, Height = 100 }, null, 1 << 20, LogoFit.Fit);
        var rs = BootLogo.Replace(demo, jpg, wrongSize.Bytes);
        rep.Check(!rs.Ok && rs.Error.Contains("Resolution"), "safety: wrong resolution refused — " + rs.Error);
        var big = conv2.Bytes.Concat(new byte[jpg.Slot]).ToArray();
        var rb = BootLogo.Replace(demo, jpg, big);
        rep.Check(!rb.Ok && rb.Error.Contains("available"), "safety: oversize file refused — " + rb.Error);
        var noisy = LogoImaging.Convert(UserPicture(320, 120, noisy: true), jpg.Info, jpg.Bytes, 6000, LogoFit.Fit);
        rep.Check(!noisy.Ok && noisy.Checks.Any(c => !c.ok && c.text.StartsWith("Size")), "safety: picture that cannot fit even at low JPEG quality is refused");
        var bg = (byte[])demo.Clone();
        long fit = 0xF00000;
        uint addr = (uint)(0x100000000L - bg.Length + fit);
        BitConverter.GetBytes(addr).CopyTo(bg, bg.Length - 0x40);
        Array.Clear(bg, (int)fit, 64);
        "_FIT_   "u8.ToArray().CopyTo(bg, fit);
        bg[fit + 8] = 3;
        bg[fit + 16 + 14] = 0x0B;
        bg[fit + 32 + 14] = 0x0C;
        var prot = BootLogo.DetectProtection(bg);
        rep.Check(prot.BootGuard, "safety: Intel Boot Guard detected from FIT manifests");

        rep.Section("Boot logo: format conversions");
        foreach (var (fmt, bpp) in new[] { ("BMP", 1), ("BMP", 4), ("BMP", 8), ("BMP", 32), ("GIF", 8), ("PNG", 24) })
        {
            var t = new PictureInfo { Format = fmt, Width = 160, Height = 64, Bpp = bpp };
            var c = LogoImaging.Convert(UserPicture(400, 200), t, null, 1 << 20, LogoFit.Fit);
            rep.Check(c.Ok && c.Info.Format == fmt && (fmt != "BMP" || c.Info.Bpp == bpp), $"convert: {fmt}{(fmt == "BMP" ? $" {bpp}-bit" : "")} {c.Bytes?.Length:N0} bytes {(c.Ok ? "" : c.Error)}");
        }
    }
}
