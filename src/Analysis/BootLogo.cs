namespace BiosFlashStudio.Analysis;

/// <summary>Header facts about an embedded picture.</summary>
public sealed class PictureInfo
{
    public string Format = "";      // BMP, JPEG, PNG, GIF
    public int Width, Height;
    public int Bpp;                 // BMP bits per pixel / PNG bit depth × channels
    public int BmpCompression;      // BI_RGB = 0
    public bool Progressive;        // JPEG
    public int Frames = 1;          // GIF
    public bool TopDown;            // BMP with negative height
    public long Length;
    public string Summary => Format switch
    {
        "BMP" => $"BMP {Width}×{Height}, {Bpp}-bit{(BmpCompression != 0 ? $", compression {BmpCompression}" : "")}",
        "JPEG" => $"JPEG {Width}×{Height}{(Progressive ? ", progressive" : ", baseline")}",
        "GIF" => $"GIF {Width}×{Height}{(Frames > 1 ? $", {Frames} frames" : "")}",
        _ => $"{Format} {Width}×{Height}",
    };
}

/// <summary>A boot logo (or other picture) found in the firmware.</summary>
public sealed class LogoEntry
{
    public UefiBuffer Buffer;
    public long Offset;
    public PictureInfo Info;
    /// <summary>Bytes available for a replacement (picture + trailing space up to the end of its section/file).</summary>
    public long Slot;
    public UefiNode Container;
    public bool Compressed;
    /// <summary>Compression of the nearest compressed ancestor ("LZMA", "EFI", "Tiano"), empty when stored plain.</summary>
    public string Compression = "";
    public bool Replaceable;
    public string Reason = "";
    public string Location = "";
    public byte[] Bytes;
    public string Title => $"{Info.Summary} · {Flash.ChipInfo.FormatSize(Info.Length)}";
    public string Where => (Compressed ? $"Compressed ({Compression}) · " : "Uncompressed · ") + Location;
}

public sealed class ProtectionInfo
{
    public bool HasFit;
    public bool BootGuard;
    public string Text = "";
}

public sealed class LogoReplaceResult
{
    public bool Ok;
    public string Error = "";
    public byte[] Image;
    public List<(bool ok, string text)> Checks = new();
    /// <summary>RAW filler sections created when a recompressed LZMA section became shorter.</summary>
    public int AddedSections;
    /// <summary>When the logo's file grew into the volume's free space: its new end offset in the flash image.</summary>
    public long GrownEnd = -1;
    internal UefiNode GrownFile;
}

/// <summary>Finds, extracts and replaces boot logos — uncompressed or inside LZMA / EFI / Tiano compressed sections.</summary>
public static class BootLogo
{
    static uint U32(byte[] d, long o) => o >= 0 && o + 4 <= d.Length ? BitConverter.ToUInt32(d, (int)o) : 0;
    static ushort U16(byte[] d, long o) => o >= 0 && o + 2 <= d.Length ? BitConverter.ToUInt16(d, (int)o) : (ushort)0;
    static int BE16(byte[] d, long o) => d[o] << 8 | d[o + 1];
    static uint BE32(byte[] d, long o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);

    // ================================================================== picture headers
    /// <summary>Validates a picture at <paramref name="o"/> and returns its exact length and properties (null if not a valid picture).</summary>
    public static PictureInfo Probe(byte[] d, long o)
    {
        try
        {
            if (o + 32 > d.Length) return null;
            if (d[o] == 'B' && d[o + 1] == 'M') return ProbeBmp(d, o);
            if (d[o] == 0xFF && d[o + 1] == 0xD8 && d[o + 2] == 0xFF) return ProbeJpeg(d, o);
            if (d[o] == 0x89 && d[o + 1] == 'P' && d[o + 2] == 'N' && d[o + 3] == 'G' && d[o + 4] == 0x0D && d[o + 5] == 0x0A) return ProbePng(d, o);
            if (d[o] == 'G' && d[o + 1] == 'I' && d[o + 2] == 'F' && d[o + 3] == '8' && (d[o + 4] == '7' || d[o + 4] == '9') && d[o + 5] == 'a') return ProbeGif(d, o);
        }
        catch (IndexOutOfRangeException) { }
        catch (ArgumentException) { }
        return null;
    }

    public static PictureInfo Probe(byte[] file) => Probe(file, 0);

    static bool DimsOk(int w, int h) => w >= 8 && h >= 8 && w <= 8192 && h <= 8192;

    static PictureInfo ProbeBmp(byte[] d, long o)
    {
        uint fileSize = U32(d, o + 2), dataOff = U32(d, o + 10), dib = U32(d, o + 14);
        if (dib is not (40 or 52 or 56 or 108 or 124)) return null;
        int w = BitConverter.ToInt32(d, (int)o + 18), h = BitConverter.ToInt32(d, (int)o + 22);
        int planes = U16(d, o + 26), bpp = U16(d, o + 28);
        uint comp = U32(d, o + 30);
        if (planes != 1 || bpp is not (1 or 4 or 8 or 16 or 24 or 32) || comp > 3) return null;
        int ah = Math.Abs(h);
        if (!DimsOk(w, ah)) return null;
        long row = ((long)w * bpp + 31) / 32 * 4;
        if (dataOff < 14 + dib || fileSize < dataOff || fileSize > 64L * 1024 * 1024 || o + fileSize > d.Length) return null;
        if (comp == 0 && dataOff + row * ah > fileSize) return null;
        return new PictureInfo { Format = "BMP", Width = w, Height = ah, Bpp = bpp, BmpCompression = (int)comp, TopDown = h < 0, Length = fileSize };
    }

    static PictureInfo ProbeJpeg(byte[] d, long o)
    {
        long p = o + 2;
        int w = 0, h = 0;
        bool prog = false;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF) return null;
            byte m = d[p + 1];
            if (m == 0xFF) { p++; continue; }
            if (m == 0xD9) return null;
            int len = BE16(d, p + 2);
            if (len < 2) return null;
            if (m is 0xC0 or 0xC1 or 0xC2 or 0xC3)
            {
                h = BE16(d, p + 5); w = BE16(d, p + 7);
                prog = m == 0xC2;
            }
            if (m == 0xDA)
            {
                long q = p + 2 + len;
                while (q + 1 < d.Length)
                {
                    if (d[q] == 0xFF && d[q + 1] != 0x00 && (d[q + 1] < 0xD0 || d[q + 1] > 0xD7))
                    {
                        if (d[q + 1] == 0xD9)
                        {
                            if (!DimsOk(w, h)) return null;
                            return new PictureInfo { Format = "JPEG", Width = w, Height = h, Progressive = prog, Bpp = 24, Length = q + 2 - o };
                        }
                        if (d[q + 1] == 0xFF) { q++; continue; }
                        // another marker (progressive scans): continue walking segments from here
                        p = q;
                        goto next;
                    }
                    q++;
                }
                return null;
            }
            p += 2 + len;
        next:;
        }
        return null;
    }

    static PictureInfo ProbePng(byte[] d, long o)
    {
        long p = o + 8;
        if (BE32(d, p) != 13 || d[p + 4] != 'I' || d[p + 5] != 'H' || d[p + 6] != 'D' || d[p + 7] != 'R') return null;
        int w = (int)BE32(d, p + 8), h = (int)BE32(d, p + 12);
        int depth = d[p + 16], ctype = d[p + 17];
        if (!DimsOk(w, h)) return null;
        while (p + 12 <= d.Length)
        {
            uint len = BE32(d, p);
            if (len > 0x7FFFFFFF || p + 12 + len > d.Length) return null;
            bool iend = d[p + 4] == 'I' && d[p + 5] == 'E' && d[p + 6] == 'N' && d[p + 7] == 'D';
            p += 12 + len;
            if (iend)
            {
                int ch = ctype switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };
                return new PictureInfo { Format = "PNG", Width = w, Height = h, Bpp = depth * ch, Length = p - o };
            }
        }
        return null;
    }

    static PictureInfo ProbeGif(byte[] d, long o)
    {
        int w = U16(d, o + 6), h = U16(d, o + 8);
        if (!DimsOk(w, h)) return null;
        byte packed = d[o + 10];
        long p = o + 13;
        if ((packed & 0x80) != 0) p += 3 * (1 << ((packed & 7) + 1));
        int frames = 0;
        while (p < d.Length)
        {
            byte b = d[p++];
            if (b == 0x3B) return frames > 0 ? new PictureInfo { Format = "GIF", Width = w, Height = h, Bpp = 8, Frames = frames, Length = p - o } : null;
            if (b == 0x21) { p++; p = SkipSubBlocks(d, p); if (p < 0) return null; continue; }
            if (b == 0x2C)
            {
                byte ip = d[p + 8];
                p += 9;
                if ((ip & 0x80) != 0) p += 3 * (1 << ((ip & 7) + 1));
                p++;                       // LZW minimum code size
                p = SkipSubBlocks(d, p);
                if (p < 0) return null;
                frames++;
                continue;
            }
            return null;
        }
        return null;
    }

    static long SkipSubBlocks(byte[] d, long p)
    {
        while (p < d.Length)
        {
            int n = d[p++];
            if (n == 0) return p;
            p += n;
        }
        return -1;
    }

    // ================================================================== protection
    /// <summary>Detects an Intel FIT with Boot Guard manifests (modified firmware would fail verification).</summary>
    public static ProtectionInfo DetectProtection(byte[] d)
    {
        var r = new ProtectionInfo();
        if (d.Length < 0x100000) { r.Text = "Image too small to check for Intel Boot Guard."; return r; }
        uint ptr = U32(d, d.Length - 0x40);
        long off = ptr == 0 || ptr == 0xFFFFFFFF ? -1 : ptr - (0x100000000L - d.Length);
        if (off < 0 || off + 16 > d.Length || BiosAnalyzer.IndexOf(d, "_FIT_   "u8, off, off + 8) != off)
        {
            r.Text = "No Intel Firmware Interface Table — Boot Guard is not in use.";
            return r;
        }
        r.HasFit = true;
        int n = (int)(U32(d, off + 8) & 0xFFFFFF);
        int km = 0, bpm = 0;
        for (int i = 1; i < Math.Min(n, 256); i++)
        {
            long e = off + i * 16;
            if (e + 16 > d.Length) break;
            int type = d[e + 14] & 0x7F;
            if (type == 0x0B) km++;
            if (type == 0x0C) bpm++;
        }
        r.BootGuard = km > 0 || bpm > 0;
        r.Text = r.BootGuard
            ? $"Intel Boot Guard manifests found in the FIT (key manifest ×{km}, boot policy ×{bpm}). The firmware is cryptographically verified at power-on — modifying it will most likely prevent the board from booting."
            : "Intel FIT present without Boot Guard manifests.";
        return r;
    }

    // ================================================================== scanning
    public static (List<LogoEntry> logos, Uefi.Tree tree) Scan(byte[] image)
    {
        var tree = Uefi.Parse(image);
        var list = new List<LogoEntry>();
        foreach (var buf in tree.Buffers)
        {
            var d = buf.Data;
            for (long i = 0; i + 32 < d.Length; i++)
            {
                byte b = d[i];
                if (b != 'B' && b != 0xFF && b != 0x89 && b != 'G') continue;
                var info = Probe(d, i);
                if (info == null) continue;
                var e = Describe(tree, buf, i, info);
                list.Add(e);
                i += info.Length - 1;
            }
        }
        // logos first (default logo GUID / names containing "logo"), then by size
        list = list.OrderByDescending(e => e.Container?.File is { } f && (f.Guid == Uefi.DefaultLogoGuid || f.Name.Contains("logo", StringComparison.OrdinalIgnoreCase)))
                   .ThenByDescending(e => (long)e.Info.Width * e.Info.Height).ToList();
        return (list, tree);
    }

    static LogoEntry Describe(Uefi.Tree tree, UefiBuffer buf, long off, PictureInfo info)
    {
        var e = new LogoEntry { Buffer = buf, Offset = off, Info = info, Compressed = buf.Owner != null, Compression = buf.Owner?.Encoding ?? "" };
        e.Bytes = buf.Data.AsSpan((int)off, (int)info.Length).ToArray();
        var node = Uefi.Locate(tree, buf, off, info.Length);
        e.Container = node;
        if (node == null)
        {
            e.Slot = info.Length;
            e.Location = $"raw flash @0x{off:X8} (outside UEFI volumes)";
            e.Replaceable = !e.Compressed;
            e.Reason = "Not inside a UEFI file — the replacement must not be larger than the original picture.";
            return e;
        }
        var file = node.File;
        e.Location = $"{(file != null ? (file.Name != "" ? $"'{file.Name}'" : "{" + file.Guid.ToString().ToUpperInvariant() + "}") : node.TypeName)} @0x{off:X}";
        // slot: from the picture to the end of the leaf payload that holds it
        long dataEnd = node.Kind == UefiKind.Section ? node.End : node.Kind == UefiKind.File ? node.End : off + info.Length;
        e.Slot = Math.Max(info.Length, dataEnd - off);
        bool leafOk = node.Kind == UefiKind.File && node.Type == 0x01                                    // raw file
                      || node.Kind == UefiKind.Section && node.Type is 0x19 or 0x18;                       // raw / freeform section
        if (!leafOk)
        {
            e.Replaceable = false;
            e.Reason = node.Kind == UefiKind.Section && node.Type is 0x10 or 0x12
                ? "Picture is embedded inside a code module (PE/TE image) — not replaceable."
                : $"Picture sits in a {node.TypeName} — not a logo container.";
            return e;
        }
        // every compressed ancestor must be LZMA (rebuildable)
        for (var x = node; x != null; x = x.Parent)
            if (x.Kind == UefiKind.Section && x.Encoding.StartsWith("unsupported"))
            {
                e.Reason = $"Inside a {x.Encoding.Replace("unsupported ", "")} section — extraction only.";
                return e;
            }
        e.Replaceable = true;
        e.Reason = !e.Compressed ? "Stored uncompressed."
            : e.Compression == "LZMA" ? "Inside an LZMA-compressed volume — the volume will be recompressed after the change."
            : $"Inside a {e.Compression}-compressed section (AMI Aptio 4 style) — it will be recompressed after the change.";
        return e;
    }

    // ================================================================== replacement
    /// <summary>
    /// Replaces <paramref name="logo"/> with <paramref name="picture"/> (which must already be converted to the same format and
    /// resolution), rebuilds every enclosing structure (checksums, CRC32 sections, LZMA recompression) and verifies the result.
    /// </summary>
    public static LogoReplaceResult Replace(byte[] image, LogoEntry logo, byte[] picture)
    {
        var res = new LogoReplaceResult();
        var newInfo = Probe(picture);
        if (newInfo == null) { res.Error = "The new picture is not a valid BMP/JPEG/PNG/GIF file."; return res; }
        if (newInfo.Format != logo.Info.Format) { res.Error = $"Format mismatch: firmware expects {logo.Info.Format}, got {newInfo.Format}."; return res; }
        if (newInfo.Width != logo.Info.Width || newInfo.Height != logo.Info.Height) { res.Error = $"Resolution mismatch: firmware expects {logo.Info.Width}×{logo.Info.Height}, got {newInfo.Width}×{newInfo.Height}."; return res; }
        if (picture.Length > logo.Slot) { res.Error = $"The new picture is {picture.Length:N0} bytes; only {logo.Slot:N0} bytes are available."; return res; }
        if (!logo.Replaceable) { res.Error = logo.Reason; return res; }

        // work on copies of every buffer along the path
        var copies = new Dictionary<UefiBuffer, byte[]>();
        byte[] Copy(UefiBuffer b) => copies.TryGetValue(b, out var c) ? c : copies[b] = (byte[])b.Data.Clone();

        var buf = logo.Buffer;
        var data = Copy(buf);
        Buffer.BlockCopy(picture, 0, data, (int)logo.Offset, picture.Length);
        Array.Fill(data, (byte)0x00, (int)logo.Offset + picture.Length, (int)(logo.Slot - picture.Length));
        if (logo.Container == null)
        {
            res.Image = data;
            return Verify(image, logo, picture, res, null);
        }

        var node = logo.Container;
        UefiNode outermostFile = null;
        while (node != null)
        {
            var d = Copy(node.Buf);
            if (node.Kind == UefiKind.File) { Uefi.FixFileChecksums(d, node, res.GrownFile == node ? res.GrownEnd - node.Off : node.Len); outermostFile = node; }
            if (node.Kind == UefiKind.Section && node.Encoding == "CRC32")
                BitConverter.GetBytes(Uefi.Crc32(d, node.DataOff, node.End - node.DataOff)).CopyTo(d, node.Off + (node.Len >= 0xFFFFFF ? 8 : 4) + 20);
            if (node.Kind == UefiKind.Section && node.Inner != null && node.Encoding is "EFI" or "Tiano")
            {
                var err = RecompressEfi(image, node, Copy(node.Inner), d, res);
                if (err != null) { res.Error = err; return res; }
            }
            else if (node.Kind == UefiKind.Section && node.Inner != null)
            {
                // Recompress the child buffer. The stream is stored with its exact length (no padding after it, so even
                // strict LZMA decoders accept it); the section shrinks and the freed bytes become a separate RAW filler
                // section, so no other section or file moves.
                var inner = Copy(node.Inner);
                long room = node.End - node.DataOff;
                byte[] chosen = null, smallest = null;
                long aligned = 0, gap = -1;
                foreach (var s in Uefi.LzmaVariants(inner, Uefi.LzmaDictionary(node.Buf.Data, node.DataOff)))
                {
                    if (smallest == null || s.Length < smallest.Length) smallest = s;
                    if (s.Length > room) continue;
                    long end = node.DataOff + s.Length;
                    long al = node.Off + ((end - node.Off + 3) & ~3L);
                    long g = node.End - al;
                    if (g == 0 || g >= 4) { chosen = s; aligned = al; gap = g; break; }
                }
                if (chosen == null)
                {
                    res.Error = smallest.Length > room
                        ? $"After recompression the volume needs {smallest.Length - room:N0} more bytes than the original ({room:N0} available). Use a simpler picture (fewer colours/details, or a lower JPEG quality)."
                        : "Could not lay out the recompressed volume without padding the LZMA stream — try a slightly different picture.";
                    return res;
                }
                bool ext = d[node.Off] == 0xFF && d[node.Off + 1] == 0xFF && d[node.Off + 2] == 0xFF;
                long secLen = node.DataOff - node.Off + chosen.Length;
                if (ext) BitConverter.GetBytes((uint)secLen).CopyTo(d, node.Off + 4);
                else { d[node.Off] = (byte)secLen; d[node.Off + 1] = (byte)(secLen >> 8); d[node.Off + 2] = (byte)(secLen >> 16); }
                Buffer.BlockCopy(chosen, 0, d, (int)node.DataOff, chosen.Length);
                Array.Fill(d, (byte)0x00, (int)(node.DataOff + chosen.Length), (int)(node.End - node.DataOff - chosen.Length));
                if (gap >= 4)
                {
                    d[aligned] = (byte)gap; d[aligned + 1] = (byte)(gap >> 8); d[aligned + 2] = (byte)(gap >> 16); d[aligned + 3] = 0x19;   // RAW filler section
                    res.AddedSections++;
                }
                res.Checks.Add((true, $"LZMA volume recompressed: {chosen.Length:N0} of {room:N0} bytes, exact-length stream" + (gap >= 4 ? $" + {gap:N0}-byte filler section" : "")));
            }
            node = node.Parent;
        }
        res.Image = copies.TryGetValue(FindRoot(buf), out var top) ? top : image;
        return Verify(image, logo, picture, res, outermostFile);
    }

    /// <summary>
    /// Recompresses an EFI/Tiano section in place. The stream keeps the section's length when it fits (the decoder reads only
    /// the stream's own compressed size, so zero padding after it is ignored). When it does not fit and the section is the
    /// last thing in a file that is itself the last file of a top-level volume (the usual AMI logo layout), the file grows
    /// into the volume's free space — the same thing AMI's ChangeLogo does — and nothing else moves.
    /// </summary>
    static string RecompressEfi(byte[] original, UefiNode node, byte[] inner, byte[] d, LogoReplaceResult res)
    {
        var v = node.Encoding == "EFI" ? EfiCompression.Variant.Efi : EfiCompression.Variant.Tiano;
        long room = node.End - node.DataOff;
        byte[] chosen = null;
        foreach (var s in EfiCompression.Variants(inner, v))
        {
            if (chosen == null || s.Length < chosen.Length) chosen = s;
            if (chosen.Length <= room) break;
        }
        // prove the stream decodes back to exactly the new content before it goes anywhere near the image
        var back = EfiCompression.Decompress(chosen, 0, chosen.Length, v);
        if (!back.AsSpan().SequenceEqual(inner)) return $"Internal error: the {node.Encoding} stream did not decode back to the new content.";

        bool ext = d[node.Off] == 0xFF && d[node.Off + 1] == 0xFF && d[node.Off + 2] == 0xFF;
        int hdr = ext ? 8 : 4;
        if (node.Type == 0x01) BitConverter.GetBytes((uint)inner.Length).CopyTo(d, node.Off + hdr);   // UncompressedLength

        if (chosen.Length <= room)
        {
            Buffer.BlockCopy(chosen, 0, d, (int)node.DataOff, chosen.Length);
            Array.Fill(d, (byte)0x00, (int)(node.DataOff + chosen.Length), (int)(room - chosen.Length));
            res.Checks.Add((true, $"{node.Encoding} section recompressed: {chosen.Length:N0} of {room:N0} bytes"));
            return null;
        }

        // ---- grow the file into the free space that follows it
        var file = node.Parent;
        var vol = file?.Parent;
        long need = chosen.Length - room;
        string why = file == null || file.Kind != UefiKind.File || ext || file.LargeFile ? "the section layout is not supported"
            : file.Children.LastOrDefault() != node ? "the compressed section is not the last section of its file"
            : file.Buf.Owner != null || vol == null || vol.Kind != UefiKind.Volume ? "the file is inside a nested volume"
            : vol.Children.LastOrDefault() != file ? "another file follows it in the volume"
            : null;
        long newSecLen = node.DataOff - node.Off + chosen.Length;
        long newFileLen = node.Off - file.Off + newSecLen;
        long newEnd = file.Off + newFileLen;
        if (why == null && (newSecLen > 0xFFFFFF || newFileLen > 0xFFFFFF)) why = "the file would exceed 16 MB";
        if (why == null && (newEnd + 7 & ~7L) > vol.End) why = $"the volume has only {vol.End - file.End:N0} bytes of free space";
        bool erased1 = (BitConverter.ToUInt32(original, (int)vol.Off + 0x2C) & 0x800) != 0;
        if (why == null)
            for (long i = file.End; i < Math.Min(vol.End, (newEnd + 7) & ~7L); i++)
                if (original[i] != (erased1 ? 0xFF : 0x00)) { why = $"the space after the file is not empty (0x{i:X})"; break; }
        if (why != null)
            return $"After recompression the logo needs {need:N0} more bytes than the original ({room:N0} available) and the file cannot grow: {why}. Use a simpler picture (fewer colours/details).";

        d[node.Off] = (byte)newSecLen; d[node.Off + 1] = (byte)(newSecLen >> 8); d[node.Off + 2] = (byte)(newSecLen >> 16);
        Buffer.BlockCopy(chosen, 0, d, (int)node.DataOff, chosen.Length);
        d[file.Off + 20] = (byte)newFileLen; d[file.Off + 21] = (byte)(newFileLen >> 8); d[file.Off + 22] = (byte)(newFileLen >> 16);
        res.GrownFile = file;
        res.GrownEnd = newEnd;
        res.Checks.Add((true, $"{node.Encoding} section recompressed: {chosen.Length:N0} bytes — logo file grew by {newEnd - file.End:N0} bytes into the volume's free space ({vol.End - newEnd:N0} bytes left)"));
        return null;
    }

    static UefiBuffer FindRoot(UefiBuffer b)
    {
        while (b.Owner != null) b = b.Owner.Buf;
        return b;
    }

    /// <summary>Re-parses the rebuilt image and proves the change is exactly what was intended.</summary>
    static LogoReplaceResult Verify(byte[] original, LogoEntry logo, byte[] picture, LogoReplaceResult res, UefiNode outermostFile)
    {
        var img = res.Image;
        if (img.Length != original.Length) { res.Error = "Image size changed."; return res; }

        // 1. nothing outside the logo's top-level file changed
        long lo = outermostFile?.Off ?? logo.Offset, hi = res.GrownEnd > 0 ? res.GrownEnd : outermostFile?.End ?? logo.Offset + logo.Slot;
        long firstDiff = -1, lastDiff = -1;
        for (long i = 0; i < img.Length; i++)
            if (img[i] != original[i]) { if (firstDiff < 0) firstDiff = i; lastDiff = i; }
        bool contained = firstDiff < 0 || firstDiff >= lo && lastDiff < hi;
        res.Checks.Add((contained, contained ? $"Changes confined to 0x{lo:X}–0x{hi - 1:X} (the logo's file)" : $"Unexpected change outside the logo's file (0x{firstDiff:X}–0x{lastDiff:X})"));

        // 2. full re-parse: same structure, all checksums valid
        var (logos, tree) = Scan(img);
        var oldTree = Uefi.Parse(original);
        int oldNodes = Uefi.All(oldTree).Count(), newNodes = Uefi.All(tree).Count();
        res.Checks.Add((newNodes == oldNodes + res.AddedSections, $"Firmware structure re-parsed: {newNodes} nodes (was {oldNodes}{(res.AddedSections > 0 ? $" + {res.AddedSections} filler" : "")})"));
        var badFiles = Uefi.All(tree).Where(n => n.Kind == UefiKind.File && n.Type != 0xF0).Where(f => !Uefi.FileChecksumsOk(f.Buf.Data, f)).ToList();
        var oldBad = Uefi.All(oldTree).Where(n => n.Kind == UefiKind.File && n.Type != 0xF0).Count(f => !Uefi.FileChecksumsOk(f.Buf.Data, f));
        res.Checks.Add((badFiles.Count <= oldBad, badFiles.Count == 0 ? "All file checksums valid" : $"{badFiles.Count} file(s) with checksum errors (original had {oldBad})"));
        bool crcOk = Uefi.All(tree).Where(n => n.Encoding == "CRC32").All(s => U32(s.Buf.Data, s.Off + 24) == Uefi.Crc32(s.Buf.Data, s.DataOff, s.End - s.DataOff));
        res.Checks.Add((crcOk, "CRC32 sections valid"));
        var lzBad = Uefi.All(tree).Where(n => n.Encoding.StartsWith("LZMA (") || n.Encoding.StartsWith("EFI/Tiano (")).Select(n => n.Encoding).ToList();
        bool lzOk = lzBad.Count == 0;
        res.Checks.Add((lzOk, lzOk ? "Every compressed section decompresses" : "Decompression problem: " + string.Join("; ", lzBad)));

        // 3. the new picture is where the old one was, byte-exact, and decodable by header
        var found = logos.FirstOrDefault(e => e.Compressed == logo.Compressed && e.Offset == logo.Offset && e.Buffer.Depth == logo.Buffer.Depth);
        bool same = found != null && found.Bytes.AsSpan().SequenceEqual(picture);
        res.Checks.Add((same, same ? $"New logo found in the rebuilt firmware ({found.Info.Summary})" : "New logo NOT found at the expected place"));

        res.Ok = res.Checks.All(c => c.ok);
        if (!res.Ok) res.Error = "Verification failed: " + string.Join("; ", res.Checks.Where(c => !c.ok).Select(c => c.text));
        return res;
    }
}
