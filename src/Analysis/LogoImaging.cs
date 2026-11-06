using SkiaSharp;

namespace BiosFlashStudio.Analysis;

public enum LogoFit { Fit, Fill, Stretch }

public sealed class LogoConversion
{
    public bool Ok;
    public string Error = "";
    public byte[] Bytes;
    public PictureInfo Info;
    public List<(bool ok, string text)> Checks = new();
    public List<string> Notes = new();
}

/// <summary>
/// Converts any picture into a drop-in replacement for an existing firmware logo: same format, resolution
/// and colour depth, small enough for the slot, and verified by decoding it again.
/// </summary>
public static class LogoImaging
{
    public static SKBitmap Decode(byte[] data) => SKBitmap.Decode(data);

    /// <summary>PNG rendering of any picture for UI previews (null if it cannot be decoded).</summary>
    public static byte[] PreviewPng(byte[] data, int maxW = 640)
    {
        using var bmp = Decode(data);
        if (bmp == null) return null;
        using var img = SKImage.FromBitmap(bmp);
        using var enc = img.Encode(SKEncodedImageFormat.Png, 100);
        return enc?.ToArray();
    }

    /// <summary>Colour of the top-left pixel (used as background when the new picture is letterboxed).</summary>
    public static SKColor CornerColor(byte[] original)
    {
        using var b = Decode(original);
        return b == null ? SKColors.Black : b.GetPixel(0, 0);
    }

    static SKBitmap Compose(SKBitmap src, int w, int h, LogoFit fit, SKColor bg)
    {
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var c = new SKCanvas(dst);
        c.Clear(bg);
        float sx = (float)w / src.Width, sy = (float)h / src.Height;
        SKRect r;
        if (fit == LogoFit.Stretch) r = new SKRect(0, 0, w, h);
        else
        {
            float s = fit == LogoFit.Fit ? Math.Min(sx, sy) : Math.Max(sx, sy);
            float dw = src.Width * s, dh = src.Height * s;
            r = SKRect.Create((w - dw) / 2, (h - dh) / 2, dw, dh);
        }
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        using var img = SKImage.FromBitmap(src);
        c.DrawImage(img, r, paint);
        c.Flush();
        return dst;
    }

    /// <summary>Builds the replacement for <paramref name="target"/> from the user's picture.</summary>
    public static LogoConversion Convert(byte[] userPicture, PictureInfo target, byte[] originalLogo, long maxBytes, LogoFit fit, SKColor? background = null)
    {
        var r = new LogoConversion();
        using var src = Decode(userPicture);
        if (src == null) { r.Error = "Your picture could not be decoded. Use a PNG, JPEG, BMP or GIF file."; return r; }
        var srcInfo = BootLogo.Probe(userPicture);
        if (srcInfo is { Format: "GIF", Frames: > 1 }) r.Notes.Add("Animated GIF: only the first frame is used.");
        if (src.Width < target.Width / 2 || src.Height < target.Height / 2)
            r.Notes.Add($"Your picture ({src.Width}×{src.Height}) is much smaller than the logo ({target.Width}×{target.Height}) and will look blurry.");
        if (Math.Abs((double)src.Width / src.Height - (double)target.Width / target.Height) > 0.15 && fit == LogoFit.Stretch)
            r.Notes.Add("Aspect ratio differs — 'Stretch' distorts the picture; 'Fit' keeps proportions.");
        var bg = background ?? (originalLogo != null ? CornerColor(originalLogo) : SKColors.Black);
        using var canvas = Compose(src, target.Width, target.Height, fit, bg);

        byte[] bytes = null;
        switch (target.Format)
        {
            case "BMP":
                if (target.BmpCompression is not (0 or 3) || target.Bpp == 16 && target.BmpCompression == 3)
                    r.Notes.Add("The original BMP is RLE-compressed; the replacement is written uncompressed with the same bit depth.");
                bytes = WriteBmp(canvas, target.Bpp == 16 ? 24 : target.Bpp, target.TopDown);
                if (target.Bpp == 16) r.Notes.Add("16-bit BMP is written as 24-bit (better supported by UEFI decoders).");
                break;
            case "JPEG":
                using (var img = SKImage.FromBitmap(canvas))
                {
                    for (int q = 92; q >= 20; q -= 6)
                    {
                        using var enc = img.Encode(SKEncodedImageFormat.Jpeg, q);
                        bytes = enc.ToArray();
                        if (bytes.Length <= maxBytes) { if (q < 92) r.Notes.Add($"JPEG quality lowered to {q} to fit the available space."); break; }
                    }
                }
                break;
            case "PNG":
                using (var img = SKImage.FromBitmap(canvas))
                using (var enc = img.Encode(SKEncodedImageFormat.Png, 100))
                    bytes = enc.ToArray();
                break;
            case "GIF":
                bytes = WriteGif(canvas);
                break;
            default:
                r.Error = $"Unsupported target format {target.Format}.";
                return r;
        }

        // ---------------- safety checks on the produced file
        r.Bytes = bytes;
        r.Info = BootLogo.Probe(bytes);
        bool parsed = r.Info != null;
        r.Checks.Add((parsed, parsed ? $"Valid {r.Info.Format} file" : "Produced file failed header validation"));
        if (parsed)
        {
            r.Checks.Add((r.Info.Format == target.Format, $"Format matches the original ({target.Format})"));
            r.Checks.Add((r.Info.Width == target.Width && r.Info.Height == target.Height, $"Resolution matches the original ({target.Width}×{target.Height})"));
            if (target.Format == "BMP")
                r.Checks.Add((r.Info.Bpp == (target.Bpp == 16 ? 24 : target.Bpp) && r.Info.BmpCompression == 0, $"{r.Info.Bpp}-bit uncompressed BMP (original {target.Bpp}-bit)"));
            if (target.Format == "JPEG") r.Checks.Add((!r.Info.Progressive, "Baseline JPEG (decodable by every UEFI JPEG decoder)"));
            if (target.Format == "GIF") r.Checks.Add((r.Info.Frames == 1, "Single-frame GIF"));
        }
        using (var check = Decode(bytes))
            r.Checks.Add((check != null && check.Width == target.Width && check.Height == target.Height, "Decodes correctly"));
        r.Checks.Add((bytes.Length <= maxBytes, $"Size {bytes.Length:N0} of {maxBytes:N0} available bytes"));
        r.Ok = r.Checks.All(c => c.ok);
        if (!r.Ok) r.Error = string.Join("; ", r.Checks.Where(c => !c.ok).Select(c => c.text));
        return r;
    }

    // ================================================================== BMP writer
    public static byte[] EncodeBmp(SKBitmap src, int bpp, bool topDown = false) => WriteBmp(src, bpp, topDown);

    static byte[] WriteBmp(SKBitmap src, int bpp, bool topDown)
    {
        int w = src.Width, h = src.Height;
        int colors = bpp <= 8 ? 1 << bpp : 0;
        var px = Pixels(src);
        byte[] index = null;
        uint[] palette = null;
        if (colors > 0) (palette, index) = Quantize(px, colors);
        int row = (w * bpp + 31) / 32 * 4;
        int dataOff = 14 + 40 + colors * 4;
        int size = dataOff + row * h;
        var b = new byte[size];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(b, 2);
        BitConverter.GetBytes(dataOff).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(w).CopyTo(b, 18);
        BitConverter.GetBytes(topDown ? -h : h).CopyTo(b, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(b, 26);
        BitConverter.GetBytes((ushort)bpp).CopyTo(b, 28);
        BitConverter.GetBytes(row * h).CopyTo(b, 34);
        BitConverter.GetBytes(2835).CopyTo(b, 38);
        BitConverter.GetBytes(2835).CopyTo(b, 42);
        BitConverter.GetBytes(colors).CopyTo(b, 46);
        for (int i = 0; i < colors; i++)
        {
            uint c = i < palette.Length ? palette[i] : 0;
            b[54 + i * 4] = (byte)c; b[55 + i * 4] = (byte)(c >> 8); b[56 + i * 4] = (byte)(c >> 16);
        }
        for (int y = 0; y < h; y++)
        {
            int sy = topDown ? y : h - 1 - y;
            int o = dataOff + y * row;
            for (int x = 0; x < w; x++)
            {
                int i = sy * w + x;
                switch (bpp)
                {
                    case 32: { uint c = px[i]; b[o + x * 4] = (byte)c; b[o + x * 4 + 1] = (byte)(c >> 8); b[o + x * 4 + 2] = (byte)(c >> 16); b[o + x * 4 + 3] = 0xFF; break; }
                    case 24: { uint c = px[i]; b[o + x * 3] = (byte)c; b[o + x * 3 + 1] = (byte)(c >> 8); b[o + x * 3 + 2] = (byte)(c >> 16); break; }
                    case 8: b[o + x] = index[i]; break;
                    case 4: b[o + x / 2] |= (byte)(x % 2 == 0 ? index[i] << 4 : index[i]); break;
                    case 1: if (index[i] != 0) b[o + x / 8] |= (byte)(0x80 >> (x % 8)); break;
                }
            }
        }
        return b;
    }

    /// <summary>Opaque pixels as 0x00RRGGBB.</summary>
    static uint[] Pixels(SKBitmap src)
    {
        var px = new uint[src.Width * src.Height];
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                px[y * src.Width + x] = (uint)(c.Red << 16 | c.Green << 8 | c.Blue);
            }
        return px;
    }

    // ================================================================== median-cut quantizer
    static (uint[] palette, byte[] index) Quantize(uint[] px, int maxColors)
    {
        var distinct = px.Distinct().ToArray();
        uint[] pal;
        if (distinct.Length <= maxColors) pal = distinct;
        else
        {
            var boxes = new List<uint[]> { distinct };
            while (boxes.Count < maxColors)
            {
                int bi = -1, bestRange = -1, bestCh = 0;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i].Length < 2) continue;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int sh = 16 - ch * 8;
                        int mn = 255, mx = 0;
                        foreach (var c in boxes[i]) { int v = (int)(c >> sh) & 0xFF; if (v < mn) mn = v; if (v > mx) mx = v; }
                        if (mx - mn > bestRange) { bestRange = mx - mn; bi = i; bestCh = ch; }
                    }
                }
                if (bi < 0 || bestRange <= 0) break;
                int shift = 16 - bestCh * 8;
                var sorted = boxes[bi].OrderBy(c => (c >> shift) & 0xFF).ToArray();
                int mid = sorted.Length / 2;
                boxes[bi] = sorted[..mid];
                boxes.Add(sorted[mid..]);
            }
            pal = boxes.Select(bx =>
            {
                long r = 0, g = 0, b = 0;
                foreach (var c in bx) { r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; }
                return (uint)((r / bx.Length) << 16 | (g / bx.Length) << 8 | (b / bx.Length));
            }).ToArray();
        }
        var cache = new Dictionary<uint, byte>();
        var idx = new byte[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            if (!cache.TryGetValue(px[i], out var k))
            {
                int best = 0, bestD = int.MaxValue;
                for (int j = 0; j < pal.Length; j++)
                {
                    int dr = (int)((px[i] >> 16) & 0xFF) - (int)((pal[j] >> 16) & 0xFF);
                    int dg = (int)((px[i] >> 8) & 0xFF) - (int)((pal[j] >> 8) & 0xFF);
                    int db = (int)(px[i] & 0xFF) - (int)(pal[j] & 0xFF);
                    int dd = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (dd < bestD) { bestD = dd; best = j; }
                }
                k = (byte)best;
                cache[px[i]] = k;
            }
            idx[i] = k;
        }
        return (pal, idx);
    }

    // ================================================================== GIF writer (single frame, LZW)
    static byte[] WriteGif(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        var (pal, idx) = Quantize(Pixels(src), 256);
        int bits = 1;
        while ((1 << bits) < Math.Max(2, pal.Length)) bits++;
        var o = new MemoryStream();
        o.Write("GIF89a"u8);
        o.WriteByte((byte)w); o.WriteByte((byte)(w >> 8)); o.WriteByte((byte)h); o.WriteByte((byte)(h >> 8));
        o.WriteByte((byte)(0x80 | (bits - 1) << 4 | (bits - 1)));
        o.WriteByte(0); o.WriteByte(0);
        for (int i = 0; i < 1 << bits; i++)
        {
            uint c = i < pal.Length ? pal[i] : 0;
            o.WriteByte((byte)(c >> 16)); o.WriteByte((byte)(c >> 8)); o.WriteByte((byte)c);
        }
        o.WriteByte(0x2C);
        o.Write(new byte[4]);
        o.WriteByte((byte)w); o.WriteByte((byte)(w >> 8)); o.WriteByte((byte)h); o.WriteByte((byte)(h >> 8));
        o.WriteByte(0);
        int minCode = Math.Max(2, bits);
        o.WriteByte((byte)minCode);
        var lzw = Lzw(idx, minCode);
        for (int p = 0; p < lzw.Length; p += 255)
        {
            int n = Math.Min(255, lzw.Length - p);
            o.WriteByte((byte)n);
            o.Write(lzw, p, n);
        }
        o.WriteByte(0);
        o.WriteByte(0x3B);
        return o.ToArray();
    }

    static byte[] Lzw(byte[] data, int minCode)
    {
        int clear = 1 << minCode, eoi = clear + 1;
        var outp = new List<byte>();
        int acc = 0, accBits = 0;
        int codeSize = minCode + 1;
        void Emit(int code)
        {
            acc |= code << accBits;
            accBits += codeSize;
            while (accBits >= 8) { outp.Add((byte)acc); acc >>= 8; accBits -= 8; }
        }
        var dict = new Dictionary<int, int>();
        int next = eoi + 1;
        Emit(clear);
        int prefix = data.Length > 0 ? data[0] : 0;
        for (int i = 1; i < data.Length; i++)
        {
            int k = data[i];
            int key = prefix << 8 | k;
            if (dict.TryGetValue(key, out var code)) { prefix = code; continue; }
            Emit(prefix);
            if (next < 4096)
            {
                dict[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                Emit(clear);
                dict.Clear();
                next = eoi + 1;
                codeSize = minCode + 1;
            }
            prefix = k;
        }
        Emit(prefix);
        Emit(eoi);
        if (accBits > 0) outp.Add((byte)acc);
        return outp.ToArray();
    }
}
