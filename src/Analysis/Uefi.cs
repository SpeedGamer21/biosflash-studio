using System.Text;
using SharpCompress.Compressors.LZMA;

namespace BiosFlashStudio.Analysis;

public enum UefiKind { Volume, File, Section }

/// <summary>A byte buffer the UEFI tree lives in: the flash image itself or the output of a decompressed section.</summary>
public sealed class UefiBuffer
{
    public byte[] Data;
    /// <summary>Section that produced this buffer by decompression (null for the flash image).</summary>
    public UefiNode Owner;
    public int Depth;
}

/// <summary>Firmware volume, FFS file or section (PI specification).</summary>
public sealed class UefiNode
{
    public UefiKind Kind;
    public UefiBuffer Buf;
    public long Off, Len;
    /// <summary>Offset of the payload (file data / section data) inside <see cref="Buf"/>.</summary>
    public long DataOff;
    public byte Type;
    public byte Attributes;
    public Guid Guid;
    public string Name = "";
    public bool LargeFile;
    public UefiNode Parent;
    public List<UefiNode> Children = new();
    /// <summary>Decompressed content of a compression/GUID section.</summary>
    public UefiBuffer Inner;
    /// <summary>For sections: "LZMA", "CRC32", "none", or an "unsupported …" description.</summary>
    public string Encoding = "";
    public long End => Off + Len;

    public string TypeName => Kind switch
    {
        UefiKind.Volume => "Volume",
        UefiKind.File => Uefi.FileTypeName(Type),
        _ => Uefi.SectionTypeName(Type),
    };

    /// <summary>Nearest enclosing FFS file (or this node when it is a file).</summary>
    public UefiNode File
    {
        get { var n = this; while (n != null && n.Kind != UefiKind.File) n = n.Parent; return n; }
    }

    public override string ToString() => $"{TypeName} @0x{Off:X} len 0x{Len:X}" + (Name != "" ? $" '{Name}'" : "");
}

/// <summary>UEFI firmware parser / rebuilder: volumes, FFS files, sections, LZMA, checksums.</summary>
public static class Uefi
{
    public static readonly Guid LzmaGuid = new("EE4E5898-3914-4259-9D6E-DC7BD79403CF");
    public static readonly Guid LzmaF86Guid = new("D42AE6BD-1352-4BFB-909A-CA72A6EAE889");
    public static readonly Guid TianoGuid = new("A31280AD-481E-41B6-95E8-127F4C984779");
    public static readonly Guid BrotliGuid = new("3D532050-5CDA-4FD0-879E-0F7F630D5AFB");
    public static readonly Guid Crc32Guid = new("FC1BCDB0-7D31-49AA-936A-A4600D9DD083");
    public static readonly Guid DefaultLogoGuid = new("7BB28B99-61BB-11D5-9A5D-0090273FC14D");

    const byte FFS_ATTRIB_LARGE_FILE = 0x01, FFS_ATTRIB_CHECKSUM = 0x40;
    const int MaxDepth = 10;
    const long MaxDecompressed = 512L * 1024 * 1024;

    static uint U32(byte[] d, long o) => o >= 0 && o + 4 <= d.Length ? BitConverter.ToUInt32(d, (int)o) : 0;
    static ushort U16(byte[] d, long o) => o >= 0 && o + 2 <= d.Length ? BitConverter.ToUInt16(d, (int)o) : (ushort)0;
    static ulong U64(byte[] d, long o) => o >= 0 && o + 8 <= d.Length ? BitConverter.ToUInt64(d, (int)o) : 0;
    static uint U24(byte[] d, long o) => o >= 0 && o + 3 <= d.Length ? (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16) : 0;
    static long Align(long v, int a) => (v + a - 1) & ~(long)(a - 1);

    public static string FileTypeName(byte t) => t switch
    {
        0x01 => "Raw file", 0x02 => "Freeform file", 0x03 => "SEC core", 0x04 => "PEI core", 0x05 => "DXE core",
        0x06 => "PEI module", 0x07 => "DXE driver", 0x08 => "Combined PEIM/driver", 0x09 => "Application", 0x0A => "SMM module",
        0x0B => "Volume image", 0x0C => "Combined SMM/DXE", 0x0D => "SMM core", 0xF0 => "Pad file", _ => $"File type 0x{t:X2}",
    };

    public static string SectionTypeName(byte t) => t switch
    {
        0x01 => "Compressed section", 0x02 => "GUID-defined section", 0x03 => "Disposable", 0x10 => "PE32 image", 0x11 => "PIC image",
        0x12 => "TE image", 0x13 => "DXE dependency", 0x14 => "Version", 0x15 => "User interface", 0x16 => "Compat16",
        0x17 => "Volume image section", 0x18 => "Freeform subtype", 0x19 => "Raw section", 0x1B => "PEI dependency", 0x1C => "MM dependency",
        _ => $"Section type 0x{t:X2}",
    };

    // ================================================================== parsing
    public sealed class Tree
    {
        public UefiBuffer Root;
        public List<UefiNode> Volumes = new();
        public List<UefiBuffer> Buffers = new();
        public List<string> Notes = new();
        public long DecompressedBytes;
    }

    public static bool VolumeHeaderValid(byte[] d, long o)
    {
        if (o < 0 || o + 0x48 > d.Length || U32(d, o + 0x28) != 0x4856465F) return false;
        ulong len = U64(d, o + 0x20);
        ushort hlen = U16(d, o + 0x30);
        if (len < 0x48 || (long)len + o > d.Length || hlen < 0x38 || hlen > 0x1000 || (hlen & 1) != 0 || hlen > (long)len) return false;
        int sum = 0;
        for (int i = 0; i < hlen; i += 2) sum += U16(d, o + i);
        return (sum & 0xFFFF) == 0;
    }

    /// <summary>Parses every firmware volume in <paramref name="image"/> (recursively, decompressing LZMA sections).</summary>
    public static Tree Parse(byte[] image)
    {
        var t = new Tree { Root = new UefiBuffer { Data = image } };
        t.Buffers.Add(t.Root);
        for (long p = 0; p + 0x48 <= image.Length;)
        {
            long hit = BiosAnalyzer.IndexOf(image, "_FVH"u8, p + 0x28, image.Length);
            if (hit < 0) break;
            long o = hit - 0x28;
            if ((o & 7) == 0 && VolumeHeaderValid(image, o))
            {
                var v = ParseVolume(t, t.Root, o, null);
                if (v != null) { t.Volumes.Add(v); p = v.End; continue; }
            }
            p = hit - 0x28 + 8;
        }
        return t;
    }

    static UefiNode ParseVolume(Tree t, UefiBuffer b, long o, UefiNode parent)
    {
        var d = b.Data;
        if (!VolumeHeaderValid(d, o)) return null;
        var v = new UefiNode
        {
            Kind = UefiKind.Volume, Buf = b, Off = o, Len = (long)U64(d, o + 0x20), Parent = parent,
            Guid = new Guid(new ReadOnlySpan<byte>(d, (int)o + 0x10, 16)), Attributes = (byte)(U32(d, o + 0x2C) >> 8),
        };
        long hlen = U16(d, o + 0x30), ext = U16(d, o + 0x34);
        long p = o + hlen;
        if (ext != 0 && o + ext + 20 <= v.End) p = Math.Max(p, o + ext + U32(d, o + ext + 16));
        v.DataOff = p;
        bool erased1 = (U32(d, o + 0x2C) & 0x800) != 0;
        byte empty = erased1 ? (byte)0xFF : (byte)0x00;
        p = o + Align(p - o, 8);              // files are 8-byte aligned relative to the volume start
        while (p + 24 <= v.End)
        {
            if (new ReadOnlySpan<byte>(d, (int)p, 24).IndexOfAnyExcept(empty) < 0) break;   // free space
            byte attr = d[p + 19];
            bool large = (attr & FFS_ATTRIB_LARGE_FILE) != 0 && U24(d, p + 20) == 0;
            long size = large ? (long)U64(d, p + 24) : U24(d, p + 20);
            int hdr = large ? 32 : 24;
            if (size < hdr || p + size > v.End) { t.Notes.Add($"Corrupt file header at 0x{p:X} in volume @0x{o:X}"); break; }
            var f = new UefiNode
            {
                Kind = UefiKind.File, Buf = b, Off = p, Len = size, DataOff = p + hdr, Type = d[p + 18], Attributes = attr,
                Guid = new Guid(new ReadOnlySpan<byte>(d, (int)p, 16)), LargeFile = large, Parent = v,
            };
            v.Children.Add(f);
            if (f.Type != 0x01 && f.Type != 0xF0 && f.Type != 0x00)
                ParseSections(t, b, f.DataOff, f.End, f);
            p = o + Align(p + size - o, 8);
        }
        return v;
    }

    static void ParseSections(Tree t, UefiBuffer b, long start, long end, UefiNode parent)
    {
        var d = b.Data;
        long p = Align(start, 4);
        while (p + 4 <= end)
        {
            uint size = U24(d, p);
            int hdr = 4;
            if (size == 0xFFFFFF) { size = U32(d, p + 4); hdr = 8; }
            if (size < hdr || p + size > end) break;
            var s = new UefiNode { Kind = UefiKind.Section, Buf = b, Off = p, Len = size, Type = d[p + 3], DataOff = p + hdr, Parent = parent };
            parent.Children.Add(s);
            switch (s.Type)
            {
                case 0x01:   // compression section
                {
                    byte ctype = d[p + hdr + 4];
                    s.DataOff = p + hdr + 5;
                    if (ctype == 0) { s.Encoding = "none"; ParseSections(t, b, s.DataOff, s.End, s); }
                    else if (ctype == 1) DecodeEfiSection(t, b, s, null);
                    else s.Encoding = $"unsupported (compression type {ctype})";
                    break;
                }
                case 0x02:   // GUID-defined
                {
                    s.Guid = new Guid(new ReadOnlySpan<byte>(d, (int)(p + hdr), 16));
                    long dataOff = p + U16(d, p + hdr + 16);
                    ushort gattr = U16(d, p + hdr + 18);
                    s.DataOff = dataOff;
                    if (dataOff < p + hdr + 20 || dataOff > s.End) { s.Encoding = "corrupt"; break; }
                    if (s.Guid == LzmaGuid)
                    {
                        s.Encoding = "LZMA";
                        if (b.Depth >= MaxDepth) { s.Encoding = "LZMA (too deep)"; break; }
                        byte[] raw;
                        try { raw = LzmaDecompress(d, dataOff, s.End - dataOff); }
                        catch (Exception ex) { s.Encoding = "LZMA (corrupt: " + ex.Message + ")"; break; }
                        if (raw == null || t.DecompressedBytes + raw.Length > MaxDecompressed) { s.Encoding = "LZMA (too large)"; break; }
                        t.DecompressedBytes += raw.Length;
                        s.Inner = new UefiBuffer { Data = raw, Owner = s, Depth = b.Depth + 1 };
                        t.Buffers.Add(s.Inner);
                        ParseSections(t, s.Inner, 0, raw.Length, s);
                    }
                    else if (s.Guid == TianoGuid) DecodeEfiSection(t, b, s, EfiCompression.Variant.Tiano);
                    else if (s.Guid == Crc32Guid) { s.Encoding = "CRC32"; ParseSections(t, b, dataOff, s.End, s); }
                    else if ((gattr & 0x01) == 0) { s.Encoding = "none"; ParseSections(t, b, dataOff, s.End, s); }
                    else s.Encoding = s.Guid == LzmaF86Guid ? "unsupported (LZMA + x86 filter)"
                        : s.Guid == BrotliGuid ? "unsupported (Brotli)" : $"unsupported (GUID {s.Guid})";
                    break;
                }
                case 0x17:   // firmware volume image
                {
                    long o = Align(s.DataOff, 8) == s.DataOff ? s.DataOff : s.DataOff;
                    var v = ParseVolume(t, b, o, s);
                    if (v != null) s.Children.Add(v);
                    break;
                }
                case 0x15:   // user interface
                {
                    int n = (int)(s.End - s.DataOff);
                    if (n >= 2)
                    {
                        var name = Encoding.Unicode.GetString(d, (int)s.DataOff, n).TrimEnd('\0');
                        var f = s.File;
                        if (f != null && f.Name == "") f.Name = name;
                    }
                    break;
                }
                case 0x18:
                    s.DataOff = Math.Min(s.End, s.DataOff + 16);
                    break;
            }
            p = Align(p + size, 4);
        }
    }

    /// <summary>True for sections whose content is stored compressed and rebuilt on change (LZMA, EFI, Tiano).</summary>
    public static bool IsCompressed(UefiNode n) => n.Kind == UefiKind.Section && n.Inner != null && n.Encoding is "LZMA" or "EFI" or "Tiano";

    /// <summary>
    /// EFI 1.1 / Tiano section. The compression-section header does not say which of the two variants was used, so both are
    /// tried and the one whose output is a clean run of sections wins (as UEFITool does).
    /// </summary>
    static void DecodeEfiSection(Tree t, UefiBuffer b, UefiNode s, EfiCompression.Variant? only)
    {
        var d = b.Data;
        if (b.Depth >= MaxDepth) { s.Encoding = "EFI/Tiano (too deep)"; return; }
        byte[] best = null;
        EfiCompression.Variant bestV = EfiCompression.Variant.Tiano;
        foreach (var v in only.HasValue ? new[] { only.Value } : new[] { EfiCompression.Variant.Efi, EfiCompression.Variant.Tiano })
        {
            if (!EfiCompression.TryDecompress(d, s.DataOff, s.End - s.DataOff, v, out var raw)) continue;
            if (best == null || SectionsCover(raw) && !SectionsCover(best)) { best = raw; bestV = v; }
        }
        if (best == null) { s.Encoding = "EFI/Tiano (corrupt)"; return; }
        if (t.DecompressedBytes + best.Length > MaxDecompressed) { s.Encoding = "EFI/Tiano (too large)"; return; }
        t.DecompressedBytes += best.Length;
        s.Encoding = bestV == EfiCompression.Variant.Efi ? "EFI" : "Tiano";
        s.Inner = new UefiBuffer { Data = best, Owner = s, Depth = b.Depth + 1 };
        t.Buffers.Add(s.Inner);
        ParseSections(t, s.Inner, 0, best.Length, s);
    }

    /// <summary>Does <paramref name="d"/> consist of well-formed sections from start to end (padding allowed)?</summary>
    static bool SectionsCover(byte[] d)
    {
        long p = 0;
        int n = 0;
        while (p + 4 <= d.Length)
        {
            uint size = U24(d, p);
            int hdr = 4;
            if (size == 0xFFFFFF) { size = U32(d, p + 4); hdr = 8; }
            if (size < hdr || p + size > d.Length || d[p + 3] == 0 || d[p + 3] > 0x1C) return false;
            n++;
            p = Align(p + size, 4);
        }
        return n > 0;
    }

    // ================================================================== LZMA (UEFI LzmaCustomDecompress format: 5-byte props + u64 size + stream)
    public static byte[] LzmaDecompress(byte[] d, long off, long len)
    {
        if (len < 13) throw new InvalidDataException("stream too short");
        var props = new ReadOnlySpan<byte>(d, (int)off, 5).ToArray();
        ulong size = U64(d, off + 5);
        if (props[0] >= 9 * 5 * 5) throw new InvalidDataException("bad properties");
        if (size > (ulong)MaxDecompressed) throw new InvalidDataException("size too large");
        var outBuf = new byte[size];
        using var ms = new MemoryStream(d, (int)off + 13, (int)len - 13, false);
        // input size left open (-1): like the UEFI LzmaDecompress library, stop at the declared output size and ignore trailing padding
        using var lz = LzmaStream.Create(props, ms, -1, (long)size, false);
        int got = 0;
        while (got < outBuf.Length)
        {
            int n = lz.Read(outBuf, got, outBuf.Length - got);
            if (n <= 0) throw new InvalidDataException($"truncated ({got}/{size})");
            got += n;
        }
        return outBuf;
    }

    /// <summary>Compresses with several encoder settings and returns the smallest stream (with the 13-byte UEFI LZMA header).</summary>
    public static byte[] LzmaCompress(byte[] data, int dictionary) =>
        LzmaVariants(data, dictionary).Take(3).OrderBy(s => s.Length).First();

    /// <summary>LZMA streams of the same data produced with different encoder settings (lazily, strongest first).</summary>
    public static IEnumerable<byte[]> LzmaVariants(byte[] data, int dictionary)
    {
        dictionary = Math.Clamp(dictionary, 1 << 16, 1 << 26);
        foreach (int fb in new[] { 273, 128, 64, 192, 96, 48, 32, 160, 80, 40 })
        {
            var ms = new MemoryStream();
            byte[] props;
            using (var lz = LzmaStream.Create(new LzmaEncoderProperties(false, dictionary, fb), false, ms))
            {
                lz.Write(data, 0, data.Length);
                props = lz.Properties;
            }
            var body = ms.ToArray();
            var full = new byte[13 + body.Length];
            props.CopyTo(full, 0);
            BitConverter.GetBytes((ulong)data.Length).CopyTo(full, 5);
            body.CopyTo(full, 13);
            yield return full;
        }
    }

    public static int LzmaDictionary(byte[] d, long off) => (int)Math.Min(int.MaxValue, U32(d, off + 1));

    // ================================================================== checksums
    public static byte FfsHeaderChecksum(byte[] d, UefiNode f)
    {
        int hdr = f.LargeFile ? 32 : 24;
        int sum = 0;
        for (int i = 0; i < hdr; i++)
        {
            if (i == 16 || i == 17 || i == 23) continue;   // header checksum, file checksum, state
            sum += d[f.Off + i];
        }
        return (byte)(0x100 - (sum & 0xFF));
    }

    public static byte FfsDataChecksum(byte[] d, UefiNode f) => FfsDataChecksum(d, f, f.Len);

    static byte FfsDataChecksum(byte[] d, UefiNode f, long len)
    {
        if ((f.Attributes & FFS_ATTRIB_CHECKSUM) == 0) return 0xAA;
        int sum = 0;
        for (long i = f.DataOff; i < f.Off + len; i++) sum += d[i];
        return (byte)(0x100 - (sum & 0xFF));
    }

    /// <summary>Recomputes both FFS checksums; <paramref name="len"/> overrides the file length (after the file was resized).</summary>
    public static void FixFileChecksums(byte[] d, UefiNode f, long len = -1)
    {
        d[f.Off + 17] = FfsDataChecksum(d, f, len < 0 ? f.Len : len);
        d[f.Off + 16] = FfsHeaderChecksum(d, f);
    }

    public static bool FileChecksumsOk(byte[] d, UefiNode f) =>
        d[f.Off + 16] == FfsHeaderChecksum(d, f) && d[f.Off + 17] == FfsDataChecksum(d, f);

    public static uint Crc32(byte[] d, long off, long len)
    {
        uint c = 0xFFFFFFFF;
        for (long i = off; i < off + len; i++)
        {
            c ^= d[i];
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    // ================================================================== traversal helpers
    public static IEnumerable<UefiNode> Walk(UefiNode n)
    {
        yield return n;
        foreach (var c in n.Children)
            foreach (var x in Walk(c)) yield return x;
    }

    public static IEnumerable<UefiNode> All(Tree t) => t.Volumes.SelectMany(Walk);

    /// <summary>Deepest node of <paramref name="buf"/> whose range contains [off, off+len).</summary>
    public static UefiNode Locate(Tree t, UefiBuffer buf, long off, long len)
    {
        UefiNode best = null;
        foreach (var n in All(t))
            if (n.Buf == buf && n.Off <= off && off + len <= n.End && (best == null || n.Len <= best.Len)) best = n;
        return best;
    }

    /// <summary>Chain from the node up to the top-level volume (node first).</summary>
    public static List<UefiNode> PathToRoot(UefiNode n)
    {
        var list = new List<UefiNode>();
        for (var x = n; x != null; x = x.Parent) list.Add(x);
        return list;
    }

    public static string Describe(UefiNode n)
    {
        var parts = new List<string>();
        for (var x = n; x != null; x = x.Parent)
        {
            if (x.Kind == UefiKind.File) parts.Add(x.Name != "" ? $"{x.Name}" : $"{{{x.Guid.ToString().ToUpperInvariant()[..8]}…}}");
            else if (IsCompressed(x)) parts.Add(x.Encoding);
        }
        parts.Reverse();
        return string.Join(" › ", parts);
    }
}
