using System.Text;
using System.Text.RegularExpressions;

namespace BiosFlashStudio.Analysis;

/// <summary>Result of parsing an ASUS / AMI Aptio / UEFI capsule (.CAP) file.</summary>
public sealed class CapsuleInfo
{
    public string FileName = "";
    public long FileSize;
    public bool IsCapsule;
    public string Kind = "Raw image (no capsule header)";
    public Guid Guid;
    public string GuidName = "";
    public long HeaderSize;
    public uint Flags;
    public long DeclaredImageSize;
    public int RomImageOffset = -1, RomLayoutOffset = -1;
    public long BodyOffset;
    public long BodyLength;
    public long TrailerLength;
    public bool IsAsus;
    public string BoardModel = "";
    public string BiosVersion = "";
    public string BuildDate = "";
    public string BootEfiInfo = "";
    public byte[] Body = Array.Empty<byte>();
    public AnalysisResult Analysis;
    public List<InfoRow> Rows = new();
    public List<string> Warnings = new();

    public string FlagsText
    {
        get
        {
            var f = new List<string>();
            if ((Flags & 0x10000) != 0) f.Add("PERSIST_ACROSS_RESET");
            if ((Flags & 0x20000) != 0) f.Add("POPULATE_SYSTEM_TABLE");
            if ((Flags & 0x40000) != 0) f.Add("INITIATE_RESET");
            return $"0x{Flags:X8}" + (f.Count > 0 ? "  (" + string.Join(", ", f) + ")" : "");
        }
    }
}

/// <summary>One region of the merge plan: which image supplies it.</summary>
public sealed class MergeRegion
{
    public string Name { get; set; } = "";
    public long Start { get; set; }
    public long End { get; set; }
    public bool KeepFromDump { get; set; }
    public string Compare { get; set; } = "";
    public string Hint { get; set; } = "";
    public string RangeText => $"0x{Start:X8} – 0x{End:X8}";
    public string SizeText => Flash.ChipInfo.FormatSize(End - Start + 1);
}

public static class CapsuleTool
{
    static readonly (Guid g, string name, string kind)[] KnownGuids =
    {
        (new Guid("3B6686BD-0D76-4030-B70E-B5519E2FC5A0"), "EFI_CAPSULE_GUID", "UEFI capsule"),
        (new Guid("4A3CA68B-7723-48FB-803D-578CC1FEC44D"), "AMI Aptio signed capsule", "AMI Aptio capsule (signed)"),
        (new Guid("14EEBB90-890A-43DB-AED1-5D3C4588A418"), "AMI Aptio unsigned capsule", "AMI Aptio capsule"),
        (new Guid("6DCBD5ED-E82D-4C44-BDA1-7194199AD92A"), "EFI_FIRMWARE_MANAGEMENT_CAPSULE_ID", "UEFI FMP capsule"),
        (new Guid("539182B9-ABB5-4391-B69A-E3A943F72FCC"), "UEFI capsule (Intel)", "UEFI capsule"),
        (new Guid("3BE07062-1D51-45D2-832B-F093257ED461"), "Toshiba capsule", "Toshiba capsule"),
    };

    static uint U32(byte[] d, long o) => BitConverter.ToUInt32(d, (int)o);
    static ushort U16(byte[] d, long o) => BitConverter.ToUInt16(d, (int)o);
    static bool IsPow2(long v) => v > 0 && (v & (v - 1)) == 0;

    /// <summary>Parses a .CAP file. Never throws for malformed input — problems end up in <see cref="CapsuleInfo.Warnings"/>.</summary>
    public static CapsuleInfo Parse(byte[] file, string fileName)
    {
        var c = new CapsuleInfo { FileName = fileName ?? "", FileSize = file.Length };
        if (file.Length >= 0x20)
        {
            var g = new Guid(new ReadOnlySpan<byte>(file, 0, 16));
            var known = KnownGuids.FirstOrDefault(k => k.g == g);
            uint hdr = U32(file, 16), flags = U32(file, 20), img = U32(file, 24);
            bool sane = hdr >= 0x1C && hdr < file.Length && hdr <= 0x100000 && img >= hdr && img <= file.Length + 0x1000;
            if (known.name != null && sane)
            {
                c.IsCapsule = true;
                c.Guid = g; c.GuidName = known.name; c.Kind = known.kind;
                c.HeaderSize = hdr; c.Flags = flags; c.DeclaredImageSize = img;
                if (known.kind.StartsWith("AMI"))
                {
                    c.RomImageOffset = U16(file, 0x1C);
                    c.RomLayoutOffset = U16(file, 0x1E);
                }
            }
            else if (file.Length > 0x800 && IsPow2(file.Length - 0x800))
            {
                // Unknown header GUID but the classic "2^n + 2 KB" ASUS layout
                c.IsCapsule = true;
                c.Kind = "Capsule (unknown GUID, 2 KB header)";
                c.Guid = g; c.GuidName = "unknown";
                c.HeaderSize = 0x800; c.DeclaredImageSize = file.Length;
            }
        }

        if (c.IsCapsule)
        {
            c.BodyOffset = c.RomImageOffset > 0 && c.RomImageOffset >= c.HeaderSize && c.RomImageOffset < file.Length ? c.RomImageOffset : c.HeaderSize;
            long end = Math.Min(file.Length, Math.Max(c.DeclaredImageSize, c.BodyOffset));
            long len = end - c.BodyOffset;
            // Prefer a power-of-two body (flash size) when the capsule carries a trailer/signature
            if (!IsPow2(len))
            {
                long p = 1; while (p * 2 <= len) p *= 2;
                if (p >= 0x100000 && len - p < 0x40000) { c.TrailerLength = len - p; len = p; }
            }
            c.BodyLength = Math.Max(0, len);
            c.TrailerLength += file.Length - end;
        }
        else
        {
            c.BodyOffset = 0;
            c.BodyLength = file.Length;
        }
        c.Body = file.AsSpan((int)c.BodyOffset, (int)c.BodyLength).ToArray();

        // ---- identification
        var m = Regex.Match(Path.GetFileNameWithoutExtension(c.FileName), @"^(?<model>.+?)[-_]ASUS[-_](?<ver>\d{3,5})", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            c.IsAsus = true;
            c.BoardModel = m.Groups["model"].Value.Replace('_', ' ').Replace('-', ' ').Trim();
            c.BiosVersion = m.Groups["ver"].Value;
        }
        ParseBootEfi(c);
        if (!c.IsAsus && (BiosAnalyzer.IndexOf(c.Body, "ASUSTeK"u8, 0, c.Body.Length) >= 0 || BiosAnalyzer.IndexOf(c.Body, "ASUS"u8, 0, Math.Min(c.Body.Length, 0x2000000)) >= 0))
            c.IsAsus = true;

        if (c.Body.Length > 0)
        {
            try { c.Analysis = BiosAnalyzer.Analyze(c.Body); } catch { }
        }
        if (c.BoardModel == "" && c.Analysis != null) c.BoardModel = c.Analysis.Dmi1("MODEL");
        if (c.BiosVersion == "" && c.Analysis != null) c.BiosVersion = c.Analysis.BiosVersion;
        if (c.BuildDate == "" && c.Analysis != null) c.BuildDate = c.Analysis.BiosDate;

        // ---- sanity checks
        if (!c.IsCapsule) c.Warnings.Add("No capsule header found — the file is already a raw flash image (nothing to strip).");
        if (c.IsCapsule && c.DeclaredImageSize != 0 && c.DeclaredImageSize > file.Length)
            c.Warnings.Add($"Header declares {c.DeclaredImageSize:N0} bytes but the file has only {file.Length:N0} — the download may be truncated.");
        if (!IsPow2(c.BodyLength)) c.Warnings.Add($"Body size {c.BodyLength:N0} bytes is not a flash-chip size (power of two).");
        if (c.TrailerLength > 0) c.Warnings.Add($"{c.TrailerLength:N0} trailing bytes after the ROM image (signature / certificate) are not part of the flash image.");
        if (c.Analysis != null && !c.Analysis.HasDescriptor && c.Analysis.Platform.StartsWith("Intel", StringComparison.OrdinalIgnoreCase))
            c.Warnings.Add("Intel image without a flash descriptor — this capsule holds only the BIOS region; merge it into a full dump instead of flashing it alone.");

        BuildRows(c);
        return c;
    }

    static void ParseBootEfi(CapsuleInfo c)
    {
        long at = BiosAnalyzer.IndexOf(c.Body, "$BOOTEFI$"u8, 0, c.Body.Length);
        if (at < 0) return;
        var strings = new List<string>();
        var sb = new StringBuilder();
        long end = Math.Min(c.Body.Length, at + 0x100);
        for (long i = at + 9; i < end; i++)
        {
            byte b = c.Body[i];
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else
            {
                if (sb.Length >= 3) strings.Add(sb.ToString().Trim());
                sb.Clear();
            }
        }
        if (sb.Length >= 3) strings.Add(sb.ToString().Trim());
        strings = strings.Where(s => s.Length >= 3).Take(6).ToList();
        c.BootEfiInfo = string.Join(" | ", strings);
        foreach (var s in strings)
        {
            if (c.BuildDate == "" && Regex.IsMatch(s, @"^\d{2}/\d{2}/\d{4}")) c.BuildDate = s;
            else if (c.BiosVersion == "" && Regex.IsMatch(s, @"^\d{3,5}$")) c.BiosVersion = s;
            else if (c.BoardModel == "" && s.Length >= 5 && Regex.IsMatch(s, @"[A-Z]") && s.Contains(' ') == false && Regex.IsMatch(s, @"\d")) c.BoardModel = s;
        }
    }

    static void BuildRows(CapsuleInfo c)
    {
        var r = c.Rows;
        r.Add(new("File", $"{c.FileName}  ({c.FileSize:N0} bytes)"));
        r.Add(new("Format", c.Kind + (c.IsAsus ? "  • ASUS" : "")));
        if (c.IsCapsule)
        {
            r.Add(new("Header GUID", $"{c.Guid.ToString().ToUpperInvariant()}  ({c.GuidName})"));
            r.Add(new("Header size", $"0x{c.HeaderSize:X} ({c.HeaderSize:N0} bytes)"));
            r.Add(new("Flags", c.FlagsText));
            r.Add(new("Capsule image size", $"0x{c.DeclaredImageSize:X} ({c.DeclaredImageSize:N0} bytes)"));
            if (c.RomImageOffset >= 0) r.Add(new("ROM image offset", $"0x{c.RomImageOffset:X}"));
            if (c.RomLayoutOffset > 0) r.Add(new("ROM layout offset", $"0x{c.RomLayoutOffset:X}"));
        }
        r.Add(new("Flash image", $"offset 0x{c.BodyOffset:X}, {Flash.ChipInfo.FormatSize(c.BodyLength)} ({c.BodyLength:N0} bytes)"));
        if (c.BodyLength > 0)
        {
            r.Add(new("Image CRC32", $"0x{Core.Hashes.Crc32(c.Body):X8}"));
            r.Add(new("Image SHA-256", Core.Hashes.Sha256(c.Body)));
        }
        if (c.BoardModel != "") r.Add(new("Board", c.BoardModel));
        if (c.BiosVersion != "") r.Add(new("BIOS version", c.BiosVersion));
        if (c.BuildDate != "") r.Add(new("Build date", c.BuildDate));
        if (c.BootEfiInfo != "") r.Add(new("$BOOTEFI$", c.BootEfiInfo));
        var a = c.Analysis;
        if (a != null)
        {
            r.Add(new("Platform", a.Platform + (a.PlatformDetail != "" ? $" ({a.PlatformDetail})" : "")));
            r.Add(new("Flash descriptor", a.HasDescriptor ? $"IFD v{a.IfdVersion}, {a.Regions.Count} regions, {a.DescriptorLock}" : "none"));
            if (a.Me.Present) r.Add(new("ME firmware", $"{a.Me.Version} ({a.Me.Family})"));
            if (a.BiosVendor != "") r.Add(new("BIOS vendor", a.BiosVendor));
            if (a.AmdAgesa != "") r.Add(new("AMD AGESA", a.AmdAgesa));
            if (a.GbeMac != "") r.Add(new("GbE MAC (template)", a.GbeMac));
        }
    }

    /// <summary>Region plan for merging the capsule image with an existing chip dump (see <see cref="FirmwareRepair.PlanMerge"/>).</summary>
    public static List<MergeRegion> PlanMerge(CapsuleInfo cap, byte[] dump, AnalysisResult dumpAnalysis)
        => FirmwareRepair.PlanMerge(cap.Body, cap.Analysis, dump, dumpAnalysis);

    /// <summary>Copies the selected regions from <paramref name="dump"/> over a copy of the capsule image.</summary>
    public static byte[] Merge(byte[] capBody, byte[] dump, IEnumerable<MergeRegion> regions, out int copied)
        => FirmwareRepair.Merge(capBody, dump, regions, out copied);

    /// <summary>Returns true when the bytes look like a capsule that should be unwrapped before flashing.</summary>
    public static bool LooksLikeCapsule(byte[] d)
    {
        if (d.Length < 0x20) return false;
        var g = new Guid(new ReadOnlySpan<byte>(d, 0, 16));
        if (KnownGuids.Any(k => k.g == g)) return true;
        return d.Length > 0x800 && IsPow2(d.Length - 0x800);
    }
}
