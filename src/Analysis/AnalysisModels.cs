namespace BiosFlashStudio.Analysis;

public sealed class FlashRegion
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public long Start { get; set; }
    public long End { get; set; }
    public string HostAccess { get; set; } = "";
    public string MeAccess { get; set; } = "";
    public string State { get; set; } = "";
    public long Size => End - Start + 1;
    public string StartText => $"0x{Start:X8}";
    public string EndText => $"0x{End:X8}";
    public string SizeText => Flash.ChipInfo.FormatSize(Size);
}

public sealed class FptPartition
{
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "";
    public long Offset { get; set; }
    public long Length { get; set; }
    public string State { get; set; } = "";
    public string OffsetText => $"0x{Offset:X8}";
    public string LengthText => $"0x{Length:X} ({Flash.ChipInfo.FormatSize(Length)})";
}

public sealed class NvramVar
{
    public string Format { get; set; } = "";
    public string Name { get; set; } = "";
    public string Guid { get; set; } = "";
    public long Offset { get; set; }
    public int DataSize { get; set; }
    public string Attributes { get; set; } = "";
    public string State { get; set; } = "";
    public string Preview { get; set; } = "";
    public string OffsetText => $"0x{Offset:X8}";
}

public sealed class InfoRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Source { get; set; } = "";
    public InfoRow() { }
    public InfoRow(string k, string v, string s = "") { Key = k; Value = v; Source = s; }
}

public sealed class MeInfo
{
    public string Family = "";
    public string Version = "";
    public string Date = "";
    public string Release = "";
    public string Svn = "";
    public string Vcn = "";
    public string Fitc = "";
    public string ChipsetHint = "";
    public long ManifestOffset = -1;
    public bool Present => Version != "";
}

public sealed class AnalysisResult
{
    public long ImageSize;
    public string Platform = "Unknown";
    public string PlatformDetail = "";
    public bool HasDescriptor;
    public int IfdVersion;
    public string DescriptorLock = "";
    public List<FlashRegion> Regions = new();
    public MeInfo Me = new();
    public List<FptPartition> Partitions = new();
    public List<InfoRow> Dmi = new();
    public List<InfoRow> MeRows = new();
    public List<NvramVar> Nvram = new();
    public string OemKey = "";
    public string OemKeySource = "";
    /// <summary>Offset of the ACPI MSDM table (-1 if the key came from a plain string or was not found).</summary>
    public long MsdmOffset = -1;
    /// <summary>SMBIOS structures found in the image: type, offset, total length (formatted area + strings).</summary>
    public List<(int Type, long Offset, long Length)> SmbiosAreas = new();
    public string GbeMac = "";
    public string BiosVendor = "";
    public string BiosVersion = "";
    public string BiosDate = "";
    public string AmdAgesa = "";
    public byte[] BlockMap = Array.Empty<byte>();   // per 64 KB: 0 empty(FF) 1 zero 2 data
    public int BlockSize = 65536;
    public List<string> Notes = new();
    public string Report = "";
    public TimeSpan Elapsed;

    public string Dmi1(string key) => Dmi.FirstOrDefault(r => r.Key == key)?.Value ?? "";
}
