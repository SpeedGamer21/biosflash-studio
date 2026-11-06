using System.IO.Compression;
using System.Text;

namespace BiosFlashStudio.Analysis;

/// <summary>A firmware image found inside a vendor BIOS update file.</summary>
public sealed class ExtractCandidate
{
    public string Kind { get; set; } = "";
    public string Source { get; set; } = "";
    public long Offset { get; set; }
    public long Length => Data?.LongLength ?? 0;
    public string Detail { get; set; } = "";
    public int Score { get; set; }
    public byte[] Data { get; set; }
    public string OffsetText => $"0x{Offset:X8}";
    public string SizeText => Flash.ChipInfo.FormatSize(Length);
    public string Crc => Data != null ? $"{Core.Hashes.Crc32(Data):X8}" : "";
}

public sealed class ExtractResult
{
    public string FileName = "";
    public long FileSize;
    public List<ExtractCandidate> Candidates = new();
    public List<string> Notes = new();
}

/// <summary>
/// Finds flashable images inside vendor BIOS updates: raw images, AMI/ASUS/UEFI capsules, InsydeFlash ($_IFLASH)
/// packages, Intel full-flash images (descriptor), runs of UEFI firmware volumes, and ZIP archives (recursively).
/// </summary>
public static class UpdateExtractor
{
    const uint IFD_SIG = 0x0FF0A55A;
    static uint U32(byte[] d, long o) => o >= 0 && o + 4 <= d.Length ? BitConverter.ToUInt32(d, (int)o) : 0;
    static ushort U16(byte[] d, long o) => o >= 0 && o + 2 <= d.Length ? BitConverter.ToUInt16(d, (int)o) : (ushort)0;
    static ulong U64(byte[] d, long o) => o >= 0 && o + 8 <= d.Length ? BitConverter.ToUInt64(d, (int)o) : 0;
    static bool IsPow2(long v) => v > 0 && (v & (v - 1)) == 0;

    public static ExtractResult Scan(byte[] f, string fileName)
    {
        var res = new ExtractResult { FileName = fileName ?? "", FileSize = f.Length };
        ScanInto(res, f, Path.GetFileName(fileName ?? ""), 0);
        // drop exact duplicates, best first
        res.Candidates = res.Candidates
            .GroupBy(c => (c.Length, c.Crc))
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score).ThenByDescending(c => c.Length)
            .ToList();
        if (res.Candidates.Count == 0 && res.Notes.Count == 0)
            res.Notes.Add("No firmware image found. If this is a self-extracting .exe, unpack it with 7-Zip first and open the extracted files.");
        return res;
    }

    static void ScanInto(ExtractResult res, byte[] f, string src, int depth)
    {
        // ---- ZIP archives (Lenovo/ASUS/MSI downloads)
        if (f.Length > 4 && f[0] == 'P' && f[1] == 'K' && f[2] == 3 && f[3] == 4 && depth < 3)
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(f), ZipArchiveMode.Read);
                foreach (var e in zip.Entries.Where(e => e.Length is > 0x10000 and < 256L * 1024 * 1024))
                {
                    using var s = e.Open();
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    ScanInto(res, ms.ToArray(), $"{src} › {e.FullName}", depth + 1);
                }
                return;
            }
            catch (Exception ex) { res.Notes.Add($"{src}: ZIP could not be read ({ex.Message})."); }
        }

        int before = res.Candidates.Count;

        // ---- capsule
        if (CapsuleTool.LooksLikeCapsule(f))
        {
            var cap = CapsuleTool.Parse(f, src);
            if (cap.IsCapsule && cap.BodyLength > 0)
                res.Candidates.Add(new ExtractCandidate
                {
                    Kind = cap.Kind, Source = src, Offset = cap.BodyOffset, Data = cap.Body, Score = 80,
                    Detail = $"header 0x{cap.HeaderSize:X}" + (cap.BoardModel != "" ? $" · {cap.BoardModel}" : "") + (cap.BiosVersion != "" ? $" · BIOS {cap.BiosVersion}" : ""),
                });
        }

        // ---- raw image (file is already a flash image)
        if (IsPow2(f.Length) && f.Length >= 0x80000 && (U32(f, 0x10) == IFD_SIG || HasFvAt(f, 0)))
            res.Candidates.Add(new ExtractCandidate { Kind = "Raw flash image", Source = src, Offset = 0, Data = f, Score = 90, Detail = "the file is already a flashable image" });

        // ---- InsydeFlash ($_IFLASH_xxx blocks)
        long p = 0;
        while ((p = BiosAnalyzer.IndexOf(f, "$_IFLASH"u8, p, f.Length)) >= 0)
        {
            string tag = Encoding.ASCII.GetString(f, (int)p + 8, Math.Min(8, f.Length - (int)p - 8)).TrimEnd('\0', ' ');
            uint total = U32(f, p + 0x10), size = U32(f, p + 0x14);
            long dataAt = p + 0x18;
            if (size == 0 || size > total || dataAt + size > f.Length) size = total;
            if (size > 0 && dataAt + size <= f.Length && size < 0x10000000)
            {
                var data = f.AsSpan((int)dataAt, (int)size).ToArray();
                var (kind, score) = tag switch
                {
                    "_BIOSIMG" => ("InsydeFlash BIOS image", 85),
                    "_EC_IMG_" or "_EC_IMG" => ("InsydeFlash EC firmware", 40),
                    "_INI_IMG" => ("InsydeFlash platform.ini", 10),
                    _ => ($"InsydeFlash block {tag}", 15),
                };
                if (tag == "_INI_IMG" || size >= 0x1000)
                    res.Candidates.Add(new ExtractCandidate { Kind = kind, Source = src, Offset = dataAt, Data = data, Score = score, Detail = $"tag $_IFLASH{tag}, {size:N0} bytes" });
            }
            p += 8;
        }

        // ---- Intel full flash images (descriptor signature at +0x10)
        p = 0;
        while ((p = BiosAnalyzer.IndexOf(f, new byte[] { 0x5A, 0xA5, 0xF0, 0x0F }, p, f.Length)) >= 0)
        {
            long start = p - 0x10;
            p += 4;
            if (start < 0) continue;
            long size = IfdImageSize(f, start);
            if (size <= 0) continue;
            if (start == 0 && size == f.Length) continue;   // already reported as raw image
            if (start + size > f.Length)
            {
                res.Notes.Add($"{src}: Intel descriptor at 0x{start:X} describes {Flash.ChipInfo.FormatSize(size)} but only {Flash.ChipInfo.FormatSize(f.Length - start)} follow (partial image).");
                continue;
            }
            res.Candidates.Add(new ExtractCandidate { Kind = "Intel full flash image", Source = src, Offset = start, Data = f.AsSpan((int)start, (int)size).ToArray(), Score = 95, Detail = "flash descriptor + all regions" });
        }

        // ---- UEFI firmware volume runs (BIOS region only)
        var fvs = FindVolumes(f);
        var covered = res.Candidates.Where(c => c.Source == src && c.Kind != "InsydeFlash platform.ini").Select(c => (c.Offset, c.Offset + c.Length)).ToList();
        foreach (var run in GroupRuns(fvs))
        {
            long a = run.start, b = run.end;
            if (covered.Any(c => a >= c.Item1 && b <= c.Item2)) continue;
            if (b - a < 0x40000) continue;
            res.Candidates.Add(new ExtractCandidate
            {
                Kind = "UEFI BIOS region (firmware volumes)", Source = src, Offset = a, Data = f.AsSpan((int)a, (int)(b - a)).ToArray(), Score = 50,
                Detail = $"{run.count} volume(s); BIOS region only — merge it into a full dump (Repair Tools)",
            });
        }

        // ---- formats that need other tools
        if (res.Candidates.Count == before)
        {
            if (BiosAnalyzer.IndexOf(f, "PFS.HDR."u8, 0, f.Length) >= 0)
                res.Notes.Add($"{src}: Dell PFS package (compressed sections) — not supported; use a Dell PFS extractor and open its output.");
            else if (BiosAnalyzer.IndexOf(f, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, 0, f.Length) >= 0)
                res.Notes.Add($"{src}: contains a 7-Zip archive — unpack the file with 7-Zip and open the extracted firmware.");
            else if (BiosAnalyzer.IndexOf(f, "MSCF"u8, 0, Math.Min(f.Length, 0x400000)) >= 0)
                res.Notes.Add($"{src}: contains a Microsoft CAB archive — unpack it (expand / 7-Zip) and open the extracted firmware.");
            else if (f.Length > 2 && f[0] == 'M' && f[1] == 'Z')
                res.Notes.Add($"{src}: Windows executable without a plain firmware image (it is compressed or encrypted, e.g. HP). Run the updater's own 'extract' option or unpack it with 7-Zip.");
        }
    }

    static bool HasFvAt(byte[] f, long o) => U32(f, o + 0x28) == 0x4856465F && FvHeaderOk(f, o);   // "_FVH"

    static bool FvHeaderOk(byte[] f, long o)
    {
        ulong len = U64(f, o + 0x20);
        ushort hlen = U16(f, o + 0x30);
        if (len < 0x48 || len > 0x8000000 || (long)len + o > f.Length || hlen < 0x48 || hlen > 0x400 || (hlen & 1) != 0) return false;
        int sum = 0;
        for (int i = 0; i < hlen; i += 2) sum += U16(f, o + i);
        return (sum & 0xFFFF) == 0;
    }

    static List<(long start, long len)> FindVolumes(byte[] f)
    {
        var list = new List<(long, long)>();
        long p = 0;
        while ((p = BiosAnalyzer.IndexOf(f, "_FVH"u8, p, f.Length)) >= 0)
        {
            long o = p - 0x28;
            p += 4;
            if (o < 0 || (o & 7) != 0 || !FvHeaderOk(f, o)) continue;
            long len = (long)U64(f, o + 0x20);
            list.Add((o, len));
            p = Math.Max(p, o + len);   // skip nested volumes
        }
        return list;
    }

    static IEnumerable<(long start, long end, int count)> GroupRuns(List<(long start, long len)> fvs)
    {
        if (fvs.Count == 0) yield break;
        long s = fvs[0].start, e = fvs[0].start + fvs[0].len;
        int n = 1;
        for (int i = 1; i < fvs.Count; i++)
        {
            if (fvs[i].start - e <= 0x10000 && fvs[i].start >= e) { e = fvs[i].start + fvs[i].len; n++; continue; }
            yield return (s, e, n);
            s = fvs[i].start; e = fvs[i].start + fvs[i].len; n = 1;
        }
        yield return (s, e, n);
    }

    /// <summary>Flash size described by an Intel descriptor at <paramref name="start"/> (component densities, cross-checked with the region limits).</summary>
    static long IfdImageSize(byte[] f, long start)
    {
        long fdb = start + 0x10;
        uint flmap0 = U32(f, fdb + 4);
        long fcba = start + ((flmap0 & 0xFF) << 4), frba = start + (((flmap0 >> 16) & 0xFF) << 4);
        if (fcba <= start || frba <= start || fcba >= start + 0x1000 || frba >= start + 0x1000) return -1;
        int nc = (int)((flmap0 >> 8) & 3);
        uint flcomp = U32(f, fcba);
        bool v2 = ((flcomp >> 17) & 7) != 0;
        long Dens(int code) => code is >= 0 and <= 7 ? 0x80000L << code : 0;
        long c1 = Dens((int)(flcomp & (v2 ? 0xFu : 7u)));
        long c2 = nc >= 1 ? Dens((int)((flcomp >> (v2 ? 4 : 3)) & (v2 ? 0xFu : 7u))) : 0;
        long byDensity = c1 + c2;
        long maxLimit = 0;
        uint mask = v2 ? 0x7FFFu : 0x1FFFu;
        for (int i = 0; i < (v2 ? 16 : 5); i++)
        {
            uint reg = U32(f, frba + i * 4);
            if (reg == 0xFFFFFFFF) continue;
            long b = (reg & mask) << 12, l = (((reg >> 16) & mask) << 12) | 0xFFF;
            if (b <= l && l < 0x10000000) maxLimit = Math.Max(maxLimit, l + 1);
        }
        if (maxLimit <= 0) return -1;
        long size = byDensity >= maxLimit && byDensity <= 0x10000000 ? byDensity : maxLimit;
        long p2 = 1; while (p2 < size) p2 <<= 1;
        return p2;
    }
}
