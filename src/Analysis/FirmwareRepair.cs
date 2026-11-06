using System.Text;

namespace BiosFlashStudio.Analysis;

/// <summary>A UEFI variable store found in an image.</summary>
public sealed class NvramStore
{
    public string Kind { get; set; } = "";
    public long Start { get; set; }
    /// <summary>Exclusive end of the store.</summary>
    public long End { get; set; }
    /// <summary>First byte after the store header (variables live in [DataStart, End)).</summary>
    public long DataStart { get; set; }
    public int Variables { get; set; }
    public long Size => End - Start;
    public override string ToString() => $"{Kind} @0x{Start:X8} ({Flash.ChipInfo.FormatSize(Size)}, {Variables} variable(s))";
}

/// <summary>An identity item (MAC, NVRAM, DMI, OEM key) that can be carried from the original dump into a repaired image.</summary>
public sealed class IdentityItem
{
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    public long DumpOffset { get; set; }
    public long StockOffset { get; set; }
    public long Length { get; set; }
    public bool Transfer { get; set; }
    public bool CanTransfer { get; set; }
    public string Status { get; set; } = "";
    /// <summary>Raw MAC for the GbE item (applied with <see cref="FirmwareRepair.SetMac"/>, not a block copy).</summary>
    public byte[] Mac { get; set; }
    public string OffsetText => CanTransfer ? $"0x{DumpOffset:X8}" + (StockOffset != DumpOffset ? $" → 0x{StockOffset:X8}" : "") : "—";
    public string SizeText => Length > 0 ? Flash.ChipInfo.FormatSize(Length) : "";
}

/// <summary>
/// Firmware repair operations that work on an in-memory image: NVRAM reset, ME clean, GbE MAC,
/// region merge with a stock image and identity transfer from the original dump.
/// </summary>
public static class FirmwareRepair
{
    static uint U32(byte[] d, long o) => o >= 0 && o + 4 <= d.Length ? BitConverter.ToUInt32(d, (int)o) : 0;
    static ushort U16(byte[] d, long o) => o >= 0 && o + 2 <= d.Length ? BitConverter.ToUInt16(d, (int)o) : (ushort)0;
    static readonly Guid AuthVssGuid = new("AAF32C78-947B-439A-A180-2E144EC37792");

    // ================================================================== NVRAM
    public static List<NvramStore> FindNvramStores(byte[] d)
    {
        var list = new List<NvramStore>();

        // VSS ("$VSS") and authenticated variable stores (GUID signature)
        for (long p = 0; p + 0x20 < d.Length; p += 4)
        {
            int hdr;
            long sizeAt;
            if (d[p] == (byte)'$' && d[p + 1] == (byte)'V' && d[p + 2] == (byte)'S' && d[p + 3] == (byte)'S') { hdr = 0x10; sizeAt = p + 4; }
            else if (d[p] == 0x78 && d[p + 1] == 0x2C && new Guid(new ReadOnlySpan<byte>(d, (int)p, 16)) == AuthVssGuid) { hdr = 0x1C; sizeAt = p + 16; }
            else continue;
            uint size = U32(d, sizeAt);
            if (d[sizeAt + 4] != 0x5A || d[sizeAt + 5] != 0xFE) continue;
            if (size < 0x100 || size > 0x400000 || p + size > d.Length) continue;
            list.Add(new NvramStore { Kind = hdr == 0x10 ? "VSS" : "VSS (auth)", Start = p, DataStart = p + hdr, End = p + size });
            p += size - 4;
        }

        // AMI NVAR: chains of consecutive "NVAR" entries
        for (long p = 0; p + 16 < d.Length;)
        {
            long hit = BiosAnalyzer.IndexOf(d, "NVAR"u8, p, d.Length);
            if (hit < 0) break;
            long q = hit;
            int n = 0;
            while (q + 10 < d.Length && d[q] == (byte)'N' && d[q + 1] == (byte)'V' && d[q + 2] == (byte)'A' && d[q + 3] == (byte)'R')
            {
                int sz = U16(d, q + 4);
                if (sz < 10 || q + sz > d.Length) break;
                q += sz;
                n++;
            }
            if (n >= 2 && !list.Any(s => hit >= s.Start && hit < s.End))
                list.Add(new NvramStore { Kind = "AMI NVAR", Start = hit, DataStart = hit, End = q, Variables = n });
            p = Math.Max(q, hit + 4);
        }

        // count VSS variables (StartId 0x55AA, state "added")
        foreach (var s in list.Where(s => s.Kind.StartsWith("VSS")))
            for (long i = s.DataStart; i + 2 < s.End; i++)
                if (d[i] == 0xAA && d[i + 1] == 0x55 && d[i + 2] == 0x3F) s.Variables++;
        return list.OrderBy(s => s.Start).ToList();
    }

    /// <summary>Erases the variables of every store (resets BIOS settings to defaults). Store headers are kept intact.</summary>
    public static long ClearNvram(byte[] d, IEnumerable<NvramStore> stores)
    {
        long n = 0;
        foreach (var s in stores)
        {
            long len = s.End - s.DataStart;
            if (len <= 0) continue;
            Array.Fill(d, (byte)0xFF, (int)s.DataStart, (int)len);
            n += len;
        }
        return n;
    }

    // ================================================================== Intel ME
    public sealed class MeCleanPlan
    {
        public bool Allowed;
        public string Reason = "";
        public int Major;
        public List<(string name, long abs, long len)> Partitions = new();
    }

    /// <summary>
    /// Plans an ME "clean": wiping the MFS (configuration file system) and FLOG partitions so the
    /// firmware re-initializes them on the next boot. Only offered for ME ≤ 10 and TXE, like other repair tools;
    /// CSME 11+ must be replaced with a clean ME region from stock firmware instead.
    /// </summary>
    public static MeCleanPlan PlanMeClean(byte[] d, AnalysisResult a)
    {
        var plan = new MeCleanPlan();
        if (a == null || (!a.Me.Present && a.Partitions.Count == 0)) { plan.Reason = "No Intel ME firmware found in the image."; return plan; }
        if (a.Me.Family.Contains("SPS")) { plan.Reason = "Server Platform Services (SPS) firmware is not supported."; return plan; }
        if (a.Me.Present && int.TryParse(a.Me.Version.Split('.')[0], out var maj)) plan.Major = maj;
        if (plan.Major >= 11)
        {
            plan.Reason = $"ME {a.Me.Version} is CSME 11 or newer — its configuration cannot be reset by wiping partitions. Use Region merge → 'Replace ME' with a clean ME region from stock firmware.";
            return plan;
        }
        var meReg = a.Regions.FirstOrDefault(x => x.Index == 2);
        long start = meReg?.Start ?? 0, end = meReg != null ? meReg.End + 1 : d.Length;
        long fpt = BiosAnalyzer.IndexOf(d, "$FPT"u8, start, Math.Min(end, start + 0x2000));
        if (fpt < 0) { plan.Reason = "ME partition table ($FPT) not found."; return plan; }
        long baseOff = meReg != null ? start : Math.Max(0, fpt - 0x10);
        foreach (var p in a.Partitions.Where(p => p.Name is "MFS" or "FLOG" && p.Length > 0 && p.Offset != 0xFFFFFFFF))
        {
            long abs = baseOff + p.Offset;
            if (abs + p.Length <= d.Length && abs >= start && abs + p.Length <= end) plan.Partitions.Add((p.Name, abs, p.Length));
        }
        if (plan.Partitions.Count == 0) { plan.Reason = "No MFS/FLOG partition in the ME partition table."; return plan; }
        plan.Allowed = true;
        plan.Reason = $"ME {(a.Me.Present ? a.Me.Version : "(version unknown)")}: wipe {string.Join(", ", plan.Partitions.Select(p => $"{p.name} ({Flash.ChipInfo.FormatSize(p.len)})"))}. The ME rebuilds its configuration on the next boot.";
        return plan;
    }

    public static void ApplyMeClean(byte[] d, MeCleanPlan plan)
    {
        if (!plan.Allowed) return;
        foreach (var (_, abs, len) in plan.Partitions) Array.Fill(d, (byte)0xFF, (int)abs, (int)len);
    }

    // ================================================================== GbE MAC
    /// <summary>Offsets of the valid Intel GbE NVM banks (word 0x13 bits 15:14 = 10b) inside the GbE region.</summary>
    public static List<long> GbeBanks(byte[] d, FlashRegion gbe)
    {
        var banks = new List<long>();
        if (gbe == null || gbe.End >= d.Length) return banks;
        long half = gbe.Size >= 0x2000 ? gbe.Size / 2 : gbe.Size;
        for (long b = gbe.Start; b + 0x80 <= gbe.End + 1; b += half)
            if ((U16(d, b + 0x26) & 0xC000) == 0x8000) banks.Add(b);
        return banks;
    }

    public static string ReadMac(byte[] d, FlashRegion gbe)
    {
        var banks = GbeBanks(d, gbe);
        long b = banks.Count > 0 ? banks[0] : gbe?.Start ?? -1;
        if (b < 0 || b + 6 > d.Length) return "";
        return string.Join(":", d.Skip((int)b).Take(6).Select(x => x.ToString("X2")));
    }

    public static byte[] ParseMac(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var hex = new string(s.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;
        var mac = Convert.FromHexString(hex);
        if (mac.All(b => b == 0) || mac.All(b => b == 0xFF) || (mac[0] & 1) != 0) return null;   // all-zero, broadcast or multicast
        return mac;
    }

    /// <summary>Writes the MAC into every valid GbE NVM bank and fixes the bank checksum (words 0x00–0x3F sum to 0xBABA).</summary>
    public static int SetMac(byte[] d, FlashRegion gbe, byte[] mac)
    {
        var banks = GbeBanks(d, gbe);
        foreach (var b in banks)
        {
            mac.CopyTo(d, b);
            int sum = 0;
            for (int w = 0; w < 0x3F; w++) sum += U16(d, b + w * 2);
            BitConverter.GetBytes((ushort)((0xBABA - sum) & 0xFFFF)).CopyTo(d, b + 0x7E);
        }
        return banks.Count;
    }

    public static bool GbeChecksumOk(byte[] d, long bank)
    {
        int sum = 0;
        for (int w = 0; w < 0x40; w++) sum += U16(d, bank + w * 2);
        return (sum & 0xFFFF) == 0xBABA;
    }

    // ================================================================== region merge
    /// <summary>
    /// Region plan for building "stock image + selected regions from the dump". Regions come from the dump's
    /// descriptor when it is valid, otherwise from the stock image (e.g. when repairing a broken descriptor).
    /// </summary>
    public static List<MergeRegion> PlanMerge(byte[] stock, AnalysisResult stockA, byte[] dump, AnalysisResult dumpA)
    {
        var list = new List<MergeRegion>();
        if (stock == null || dump == null || stock.Length != dump.Length) return list;
        var regs = (dumpA?.HasDescriptor == true ? dumpA.Regions : stockA?.Regions) ?? new List<FlashRegion>();
        foreach (var g in regs.Where(g => g.End < stock.Length && g.Start <= g.End))
        {
            bool same = stock.AsSpan((int)g.Start, (int)g.Size).SequenceEqual(dump.AsSpan((int)g.Start, (int)g.Size));
            bool dumpEmpty = dump.AsSpan((int)g.Start, (int)g.Size).IndexOfAnyExcept((byte)0xFF) < 0;
            var n = g.Name.ToUpperInvariant();
            var mr = new MergeRegion { Name = g.Name, Start = g.Start, End = g.End, Compare = same ? "identical" : dumpEmpty ? "dump empty" : "differs" };
            if (n.Contains("GBE")) { mr.KeepFromDump = !dumpEmpty; mr.Hint = "MAC address of this board"; }
            else if (n.Contains("DESCRIPTOR")) mr.Hint = "keep if the dump's descriptor was customised/unlocked";
            else if (n == "EC" || n.StartsWith("EC ")) { mr.KeepFromDump = !dumpEmpty; mr.Hint = "embedded-controller firmware"; }
            else if (n.Contains("PDR") || n.Contains("PLATFORM")) { mr.KeepFromDump = !dumpEmpty; mr.Hint = "platform data"; }
            else if (n.Contains("ME")) mr.Hint = "keep to preserve the configured ME";
            else if (n.Contains("BIOS")) mr.Hint = "system firmware";
            list.Add(mr);
        }
        return list.OrderBy(r => r.Start).ToList();
    }

    /// <summary>Copies the regions ticked "keep dump" from the dump over a copy of the stock image.</summary>
    public static byte[] Merge(byte[] stock, byte[] dump, IEnumerable<MergeRegion> regions, out int copied)
    {
        var res = (byte[])stock.Clone();
        copied = 0;
        foreach (var r in regions.Where(r => r.KeepFromDump))
        {
            Buffer.BlockCopy(dump, (int)r.Start, res, (int)r.Start, (int)(r.End - r.Start + 1));
            copied++;
        }
        return res;
    }

    // ================================================================== identity transfer
    static bool Blank(byte[] d, long off, long len) =>
        off >= 0 && off + len <= d.Length && d.AsSpan((int)off, (int)len).IndexOfAnyExcept((byte)0xFF) < 0;

    /// <summary>
    /// Finds the board identity in the original dump (GbE MAC, NVRAM stores, SMBIOS serial/UUID tables, MSDM key)
    /// and works out whether each one can be carried into the stock image without breaking its layout.
    /// </summary>
    public static List<IdentityItem> PlanIdentity(byte[] dump, AnalysisResult da, byte[] stock, AnalysisResult sa)
    {
        var items = new List<IdentityItem>();
        if (dump == null || stock == null || dump.Length != stock.Length) return items;

        // MAC
        var gd = da?.Regions.FirstOrDefault(x => x.Index == 3);
        var gs = sa?.Regions.FirstOrDefault(x => x.Index == 3) ?? gd;
        if (gd != null)
        {
            var macText = ReadMac(dump, gd);
            var mac = ParseMac(macText);
            var it = new IdentityItem { Kind = "GbE MAC address", Detail = macText, DumpOffset = gd.Start, StockOffset = gs?.Start ?? gd.Start, Length = 6, Mac = mac };
            if (mac == null) it.Status = "no valid MAC in dump";
            else if (gs == null || GbeBanks(stock, gs).Count == 0) it.Status = "stock image has no valid GbE NVM";
            else if (ReadMac(stock, gs) == macText) it.Status = "already identical";
            else { it.CanTransfer = it.Transfer = true; it.Status = $"replaces stock MAC {ReadMac(stock, gs)}"; }
            items.Add(it);
        }

        // NVRAM stores (same layout only)
        bool sameBios = da != null && sa != null && da.BiosVersion != "" && da.BiosVersion == sa.BiosVersion;
        var stockStores = FindNvramStores(stock);
        foreach (var s in FindNvramStores(dump))
        {
            var m = stockStores.FirstOrDefault(x => x.Start == s.Start && x.End == s.End && x.Kind == s.Kind);
            var it = new IdentityItem { Kind = $"NVRAM store ({s.Kind})", Detail = $"{s.Variables} variable(s) — settings, DMI written by vendor tools, boot entries", DumpOffset = s.Start, StockOffset = s.Start, Length = s.Size };
            if (m == null) it.Status = "layout differs in stock image — not transferable";
            else if (dump.AsSpan((int)s.Start, (int)s.Size).SequenceEqual(stock.AsSpan((int)s.Start, (int)s.Size))) it.Status = "already identical";
            else
            {
                it.CanTransfer = true;
                it.Transfer = sameBios;
                it.Status = sameBios ? "same BIOS version — safe to keep" : "different BIOS version — only tick if needed (old settings may not match)";
            }
            items.Add(it);
        }

        // SMBIOS identity tables (type 1 system / 2 baseboard / 3 chassis)
        var idAreas = da?.SmbiosAreas.Where(a => a.Type is 1 or 2 or 3).ToList() ?? new();
        if (idAreas.Count > 0)
        {
            long a0 = idAreas.Min(a => a.Offset), a1 = idAreas.Max(a => a.Offset + a.Length);
            long b0 = a0 & ~0xFFFL, b1 = (a1 + 0xFFF) & ~0xFFFL;
            var serial = da.Dmi1("SERIAL");
            var it = new IdentityItem { Kind = "SMBIOS / DMI tables", Detail = $"serial {(serial == "" ? "—" : serial)}, UUID {da.Dmi1("UUID")}", DumpOffset = b0, StockOffset = b0, Length = b1 - b0 };
            var st1 = sa?.SmbiosAreas.FirstOrDefault(a => a.Type == 1) ?? default;
            var dt1 = idAreas.FirstOrDefault(a => a.Type == 1);
            if (dump.AsSpan((int)b0, (int)(b1 - b0)).SequenceEqual(stock.AsSpan((int)b0, (int)(b1 - b0)))) it.Status = "already identical";
            else if (Blank(stock, b0, b1 - b0)) { it.CanTransfer = it.Transfer = true; it.Status = "DMI area is blank in stock image"; }
            else if (dt1.Length > 0 && st1.Length > 0 && st1.Offset == dt1.Offset) { it.CanTransfer = it.Transfer = true; it.Status = "same table location in stock image"; }
            else it.Status = "DMI is stored elsewhere in the stock image — transfer the NVRAM store instead";
            items.Add(it);
        }

        // MSDM (Windows OEM key)
        if (da != null && da.MsdmOffset >= 0)
        {
            var it = new IdentityItem { Kind = "Windows OEM key (MSDM)", Detail = da.OemKey, DumpOffset = da.MsdmOffset, Length = 0x55 };
            if (sa != null && sa.MsdmOffset >= 0)
            {
                it.StockOffset = sa.MsdmOffset;
                if (sa.OemKey == da.OemKey) it.Status = "already identical";
                else { it.CanTransfer = it.Transfer = true; it.Status = "replaces the stock MSDM table"; }
            }
            else if (Blank(stock, da.MsdmOffset, 0x55)) { it.StockOffset = da.MsdmOffset; it.CanTransfer = it.Transfer = true; it.Status = "OA3 area is blank in stock image"; }
            else it.Status = "no free MSDM slot at that location in the stock image";
            items.Add(it);
        }
        return items;
    }

    /// <summary>Applies the ticked identity items to <paramref name="image"/> (normally the merged stock image).</summary>
    public static List<string> ApplyIdentity(byte[] image, byte[] dump, AnalysisResult imageA, IEnumerable<IdentityItem> items)
    {
        var done = new List<string>();
        foreach (var it in items.Where(i => i.Transfer && i.CanTransfer))
        {
            if (it.Mac != null)
            {
                var g = imageA?.Regions.FirstOrDefault(x => x.Index == 3);
                if (g != null && SetMac(image, g, it.Mac) > 0) done.Add($"MAC {it.Detail}");
                continue;
            }
            if (it.DumpOffset + it.Length > dump.Length || it.StockOffset + it.Length > image.Length) continue;
            Buffer.BlockCopy(dump, (int)it.DumpOffset, image, (int)it.StockOffset, (int)it.Length);
            done.Add(it.Kind);
        }
        return done;
    }

    // ================================================================== binary helpers
    public static byte[] Resize(byte[] d, long size, byte fill = 0xFF)
    {
        var r = new byte[size];
        if (size > d.Length) Array.Fill(r, fill, d.Length, (int)(size - d.Length));
        Buffer.BlockCopy(d, 0, r, 0, (int)Math.Min(size, d.Length));
        return r;
    }

    public static string Describe(byte[] d, AnalysisResult a)
    {
        if (a == null) return $"{Flash.ChipInfo.FormatSize(d.Length)}";
        var sb = new StringBuilder($"{Flash.ChipInfo.FormatSize(d.Length)} · {a.Platform}");
        if (a.BiosVersion != "") sb.Append($" · BIOS {a.BiosVersion}");
        if (a.Me.Present) sb.Append($" · ME {a.Me.Version}");
        if (a.HasDescriptor) sb.Append($" · IFD v{a.IfdVersion}");
        return sb.ToString();
    }
}
