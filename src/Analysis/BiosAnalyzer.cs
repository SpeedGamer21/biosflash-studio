using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace BiosFlashStudio.Analysis;

/// <summary>
/// Offline firmware image analyzer: Intel Flash Descriptor, ME/CSE manifest, FPT, SMBIOS/DMI,
/// MSDM (Windows OEM key), UEFI NVRAM (VSS / AMI NVAR), GbE MAC, AMD PSP, vendor strings.
/// </summary>
public static class BiosAnalyzer
{
    const uint IFD_SIG = 0x0FF0A55A;
    static readonly string[] RegionNames =
    {
        "Descriptor", "BIOS", "Intel ME", "GbE", "Platform Data", "Device Expansion 1", "Secondary BIOS", "Microcode",
        "EC / BMC", "Device Expansion 2", "IE", "10GbE A", "10GbE B", "Reserved 13", "Reserved 14", "PTT",
    };

    static uint U32(byte[] d, long o) => o >= 0 && o + 4 <= d.Length ? BitConverter.ToUInt32(d, (int)o) : 0;
    static ushort U16(byte[] d, long o) => o >= 0 && o + 2 <= d.Length ? BitConverter.ToUInt16(d, (int)o) : (ushort)0;

    public static AnalysisResult Analyze(byte[] d)
    {
        var sw = Stopwatch.StartNew();
        var r = new AnalysisResult { ImageSize = d.Length };
        if (d.Length < 4096)
        {
            r.Notes.Add("Image too small to analyze.");
            r.Report = BuildReport(r);
            return r;
        }
        BuildBlockMap(d, r);
        if (r.BlockMap.All(b => b == 0)) r.Notes.Add("Image is completely blank (all 0xFF).");
        var strings = Strings.Extract(d, 5, 400_000);

        ParseDescriptor(d, r);
        ParseMe(d, r);
        DetectAmd(d, r, strings);
        if (r.Platform == "Unknown" && r.Me.Present) r.Platform = "Intel Platform";
        if (r.Platform.StartsWith("Intel") && r.Me.Present)
            r.PlatformDetail = $"{r.Me.Family} {r.Me.Version}";

        ParseSmbios(d, r);
        HeuristicDmi(r, strings);
        FindOemKey(d, r, strings);
        ParseNvram(d, r);
        DetectBiosVendor(d, r, strings);

        r.Elapsed = sw.Elapsed;
        r.Report = BuildReport(r);
        return r;
    }

    // ------------------------------------------------------------------ block map
    static void BuildBlockMap(byte[] d, AnalysisResult r)
    {
        int bs = d.Length >= 4 * 1024 * 1024 ? 65536 : d.Length >= 256 * 1024 ? 4096 : 256;
        r.BlockSize = bs;
        int n = (d.Length + bs - 1) / bs;
        r.BlockMap = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int off = i * bs, len = Math.Min(bs, d.Length - off);
            var s = new ReadOnlySpan<byte>(d, off, len);
            r.BlockMap[i] = s.IndexOfAnyExcept((byte)0xFF) < 0 ? (byte)0 : s.IndexOfAnyExcept((byte)0x00) < 0 ? (byte)1 : (byte)2;
        }
    }

    // ------------------------------------------------------------------ Intel descriptor
    static void ParseDescriptor(byte[] d, AnalysisResult r)
    {
        long fdb = U32(d, 0x10) == IFD_SIG ? 0x10 : U32(d, 0) == IFD_SIG ? 0 : -1;
        if (fdb < 0) return;
        r.HasDescriptor = true;
        r.Platform = "Intel Platform";
        uint flmap0 = U32(d, fdb + 4), flmap1 = U32(d, fdb + 8);
        long fcba = (flmap0 & 0xFF) << 4;
        long frba = ((flmap0 >> 16) & 0xFF) << 4;
        long fmba = (flmap1 & 0xFF) << 4;
        uint flcomp = U32(d, fcba);
        int readFreq = (int)((flcomp >> 17) & 7);
        r.IfdVersion = readFreq == 0 ? 1 : 2;
        int maxRegions = r.IfdVersion == 1 ? 5 : 16;
        uint mask = r.IfdVersion == 1 ? 0x1FFFu : 0x7FFFu;

        uint[] mstr = { U32(d, fmba), U32(d, fmba + 4), U32(d, fmba + 8) };
        for (int i = 0; i < maxRegions; i++)
        {
            long off = frba + i * 4;
            if (off + 4 > d.Length || off >= fmba && fmba > frba) break;
            uint reg = U32(d, off);
            long baseA = (reg & mask) << 12;
            long limit = (((reg >> 16) & mask) << 12) | 0xFFF;
            if (baseA > limit || reg == 0xFFFFFFFF) continue;
            var fr = new FlashRegion
            {
                Index = i,
                Name = RegionNames[i],
                Start = baseA,
                End = limit,
                HostAccess = Access(mstr[0], i, r.IfdVersion),
                MeAccess = Access(mstr[1], i, r.IfdVersion),
            };
            if (limit >= d.Length) fr.State = "beyond image!";
            else
            {
                var s = new ReadOnlySpan<byte>(d, (int)baseA, (int)(limit - baseA + 1));
                fr.State = s.IndexOfAnyExcept((byte)0xFF) < 0 ? "empty" : "data";
            }
            r.Regions.Add(fr);
        }
        bool hostWritesDesc = CanWrite(mstr[0], 0, r.IfdVersion);
        bool hostWritesMe = CanWrite(mstr[0], 2, r.IfdVersion);
        r.DescriptorLock = hostWritesDesc ? "UNLOCKED (host can write descriptor)" : "Locked (factory default)";
        if (hostWritesMe) r.Notes.Add("Host CPU has write access to the ME region (descriptor unlocked / modded).");

        var gbe = r.Regions.FirstOrDefault(x => x.Index == 3);
        if (gbe != null && gbe.Start + 6 <= d.Length)
        {
            var mac = new ReadOnlySpan<byte>(d, (int)gbe.Start, 6).ToArray();
            if (mac.Any(b => b != 0xFF) && mac.Any(b => b != 0))
                r.GbeMac = string.Join(":", mac.Select(b => b.ToString("X2")));
        }
    }

    static bool CanRead(uint m, int region, int ver) => ver == 1 ? (m >> (16 + region) & 1) != 0 : region < 12 && (m >> (8 + region) & 1) != 0;
    static bool CanWrite(uint m, int region, int ver) => ver == 1 ? (m >> (24 + region) & 1) != 0 : region < 12 && (m >> (20 + region) & 1) != 0;
    static string Access(uint m, int region, int ver)
    {
        if (ver == 1 && region > 7) return "n/a";
        return (CanRead(m, region, ver) ? "R" : "-") + (CanWrite(m, region, ver) ? "W" : "-");
    }

    /// <summary>Grants the host (BIOS/CPU), ME and GbE masters full read/write access. Returns false if no descriptor.</summary>
    public static bool UnlockDescriptor(byte[] d)
    {
        long fdb = U32(d, 0x10) == IFD_SIG ? 0x10 : U32(d, 0) == IFD_SIG ? 0 : -1;
        if (fdb < 0) return false;
        uint flmap0 = U32(d, fdb + 4), flmap1 = U32(d, fdb + 8);
        long fcba = (flmap0 & 0xFF) << 4, fmba = (flmap1 & 0xFF) << 4;
        int ver = ((U32(d, fcba) >> 17) & 7) == 0 ? 1 : 2;
        for (int i = 0; i < 3; i++)
        {
            long o = fmba + i * 4;
            uint v = U32(d, o);
            uint nv = ver == 1 ? 0xFFFF0000u | (v & 0xFFFF) : 0xFFFFFF00u | (v & 0xFF);
            BitConverter.GetBytes(nv).CopyTo(d, o);
        }
        return true;
    }

    // ------------------------------------------------------------------ Intel ME
    static void ParseMe(byte[] d, AnalysisResult r)
    {
        var meReg = r.Regions.FirstOrDefault(x => x.Index == 2);
        long start = meReg?.Start ?? 0;
        long end = meReg != null ? Math.Min(meReg.End + 1, d.Length) : d.Length;
        if (meReg != null && meReg.State == "empty")
        {
            r.Notes.Add("ME region is empty (all 0xFF) - the platform will not boot correctly until ME firmware is restored.");
            r.Me.Family = "Intel ME";
            r.MeRows.Add(new InfoRow("Status", "ME region EMPTY"));
            return;
        }

        // Flash Partition Table
        long fpt = IndexOf(d, "$FPT"u8, start, Math.Min(end, start + 0x2000));
        if (fpt >= 0)
        {
            uint n = U32(d, fpt + 4);
            long baseOff = meReg != null ? start : Math.Max(0, fpt - 0x10);
            if (n is > 0 and < 128)
            {
                r.Me.Fitc = $"{U16(d, fpt + 0x18)}.{U16(d, fpt + 0x1A)}.{U16(d, fpt + 0x1C)}.{U16(d, fpt + 0x1E)}";
                for (int i = 0; i < n; i++)
                {
                    long e = fpt + 0x20 + i * 0x20;
                    if (e + 0x20 > d.Length) break;
                    string name = Ascii(d, e, 4);
                    if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_')) break;
                    long off = U32(d, e + 8), len = U32(d, e + 12);
                    var p = new FptPartition { Name = name, Owner = Ascii(d, e + 4, 4), Offset = off, Length = len };
                    long abs = baseOff + off;
                    if (len == 0 || off == 0xFFFFFFFF) p.State = "n/a";
                    else if (abs + len > d.Length) p.State = "out of range";
                    else p.State = SpanBlank(d, abs, Math.Min(len, 0x10000)) ? "empty" : "present";
                    r.Partitions.Add(p);
                }
            }
        }

        // Manifest ($MN2 for ME 6+, $MAN for older)
        long searchStart = start, searchEnd = end;
        var ftpr = r.Partitions.FirstOrDefault(p => p.Name == "FTPR" && p.State == "present");
        if (ftpr != null && fpt >= 0)
        {
            long b = (meReg != null ? start : Math.Max(0, fpt - 0x10)) + ftpr.Offset;
            if (b < end) { searchStart = b; searchEnd = Math.Min(end, b + ftpr.Length); }
        }
        if (!FindManifest(d, r, searchStart, searchEnd) && (searchStart != start || searchEnd != end))
            FindManifest(d, r, start, end);
        if (!r.Me.Present && fpt < 0) return;

        if (!r.Me.Present) r.Me.Family = "Intel ME";
        if (r.Partitions.Any(p => p.Name is "OPR1" or "OPR2")) r.Me.Family = "Intel SPS";
        r.MeRows.Add(new InfoRow("Family", r.Me.Family));
        r.MeRows.Add(new InfoRow("Version", r.Me.Present ? r.Me.Version : "not found"));
        if (r.Me.Release != "") r.MeRows.Add(new InfoRow("Release", r.Me.Release));
        if (r.Me.Date != "") r.MeRows.Add(new InfoRow("Build Date", r.Me.Date));
        if (r.Me.Svn != "") r.MeRows.Add(new InfoRow("Security Version (SVN)", r.Me.Svn));
        if (r.Me.Vcn != "") r.MeRows.Add(new InfoRow("Version Control Number", r.Me.Vcn));
        if (r.Me.Fitc != "") r.MeRows.Add(new InfoRow("FITC Version", r.Me.Fitc));
        if (r.Me.ChipsetHint != "") r.MeRows.Add(new InfoRow("Chipset (estimate)", r.Me.ChipsetHint));
        if (meReg != null) r.MeRows.Add(new InfoRow("Region Size", meReg.SizeText));
        r.MeRows.Add(new InfoRow("FPT Partitions", r.Partitions.Count.ToString()));
        if (r.Partitions.Count > 0)
        {
            var mfs = r.Partitions.FirstOrDefault(p => p.Name is "MFS" or "MFS\0");
            if (mfs != null) r.MeRows.Add(new InfoRow("MFS (config FS)", mfs.State == "present" ? "Initialized" : mfs.State));
        }
        if (r.HasDescriptor) r.MeRows.Add(new InfoRow("Descriptor", r.DescriptorLock));
    }

    static bool FindManifest(byte[] d, AnalysisResult r, long start, long end)
    {
        foreach (var tag in new[] { "$MN2"u8.ToArray(), "$MAN"u8.ToArray() })
        {
            long t = start;
            while ((t = IndexOf(d, tag, t, end)) >= 0)
            {
                long h = t - 0x1C;
                if (h >= 0 && U32(d, h) == 4 && U32(d, h + 0x10) == 0x8086)
                {
                    int maj = U16(d, t + 8), min = U16(d, t + 10), hf = U16(d, t + 12), bld = U16(d, t + 14);
                    if (maj is >= 1 and <= 40 && min < 100)
                    {
                        var me = r.Me;
                        me.ManifestOffset = h;
                        me.Version = $"{maj}.{min}.{hf}.{bld}";
                        me.Family = maj >= 11 ? "CSE ME" : "Intel ME";
                        uint flags = U32(d, h + 0x0C);
                        me.Release = (flags & 0x80000000) != 0 ? "Pre-Production" : "Production";
                        uint date = U32(d, h + 0x14);
                        if (date != 0 && date != 0xFFFFFFFF) me.Date = $"{date >> 16:X4}-{(date >> 8) & 0xFF:X2}-{date & 0xFF:X2}";
                        uint svn = U32(d, t + 0x10), vcn = U32(d, t + 0x14);
                        if (svn < 256) me.Svn = svn.ToString();
                        if (vcn < 256) me.Vcn = vcn.ToString();
                        me.ChipsetHint = maj switch
                        {
                            6 => "6-series (Sandy Bridge)", 7 => "7-series (Ivy Bridge)", 8 => "8-series (Haswell)",
                            9 => "8/9-series (Haswell/Broadwell)", 10 => "Broadwell / 9-series", 11 => "100/200-series (Skylake/Kaby Lake)",
                            12 => "300-series (Coffee/Whiskey Lake)", 13 => "Ice Lake", 14 => "400-series / CMP (Comet Lake)",
                            15 => "500-series (Tiger/Rocket Lake)", 16 => "600/700-series (Alder/Raptor Lake)",
                            18 => "Meteor Lake", 19 => "Lunar/Arrow Lake", _ => "",
                        };
                        return true;
                    }
                }
                t += 4;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ AMD
    static void DetectAmd(byte[] d, AnalysisResult r, List<(long off, string s)> strings)
    {
        if (r.HasDescriptor) return;
        long efs = -1;
        for (long o = 0x20000; o + 4 <= d.Length; o += 0x20000)
            if (U32(d, o) == 0x55AA55AA) { efs = o; break; }
        bool psp = IndexOf(d, "$PSP"u8, 0, d.Length) >= 0;
        var agesa = strings.FirstOrDefault(s => s.s.StartsWith("AGESA!", StringComparison.Ordinal) || s.s.Contains("AGESA V", StringComparison.Ordinal));
        if (efs >= 0 || psp || agesa.s != null)
        {
            r.Platform = "AMD Platform";
            var parts = new List<string>();
            if (efs >= 0) parts.Add($"EFS @0x{efs:X}");
            if (psp) parts.Add("PSP directory");
            if (agesa.s != null) { r.AmdAgesa = agesa.s.Replace("AGESA!", "AGESA ").Trim(); parts.Add(r.AmdAgesa); }
            r.PlatformDetail = string.Join(", ", parts);
        }
    }

    // ------------------------------------------------------------------ SMBIOS / DMI
    static readonly string[] Placeholders = { "to be filled", "default string", "system manufacturer", "system product", "o.e.m.", "not applicable", "none", "123456789", "system serial", "base board" };

    static void ParseSmbios(byte[] d, AnalysisResult r)
    {
        (int score, string[] s, long off, int len) best1 = (-1, null, 0, 0), best2 = (-1, null, 0, 0), best0 = (-1, null, 0, 0), best3 = (-1, null, 0, 0);
        int limit = d.Length - 0x40;
        for (int i = 0; i < limit; i++)
        {
            byte t = d[i];
            if (t > 3) continue;
            byte len = d[i + 1];
            bool okLen = t switch
            {
                0 => len is >= 0x12 and <= 0x1A,
                1 => len is 0x08 or 0x19 or 0x1B,
                2 => len is >= 0x08 and <= 0x14,
                3 => len is >= 0x0D and <= 0x16,
                _ => false,
            };
            if (!okLen) continue;
            var strs = ReadSmbiosStrings(d, i + len);
            if (strs == null) continue;
            int[] idx = t switch { 0 => new[] { 4, 5, 8 }, 1 => new[] { 4, 5, 6, 7 }, 2 => new[] { 4, 5, 6, 7 }, _ => new[] { 4, 6, 7 } };
            int valid = 0, score = 0;
            bool bad = false;
            foreach (var k in idx)
            {
                int si = d[i + k];
                if (si > strs.Length) { bad = true; break; }
                if (si == 0) continue;
                valid++;
                var v = strs[si - 1];
                score += Placeholders.Any(p => v.Contains(p, StringComparison.OrdinalIgnoreCase)) ? 1 : 4;
            }
            if (bad || valid < 2) continue;
            var cand = (score, strs, (long)i, (int)len);
            switch (t)
            {
                case 0: if (score > best0.score) best0 = cand; break;
                case 1: if (score > best1.score) best1 = cand; break;
                case 2: if (score > best2.score) best2 = cand; break;
                case 3: if (score > best3.score) best3 = cand; break;
            }
        }
        string S((int score, string[] s, long off, int len) c, int field) { int si = d[c.off + field]; return si > 0 && si <= c.s.Length ? c.s[si - 1].Trim() : ""; }
        const string src = "SMBIOS";
        foreach (var (type, c) in new[] { (0, best0), (1, best1), (2, best2), (3, best3) })
        {
            if (c.s == null) continue;
            long e = c.off + c.len;
            while (e + 1 < d.Length && !(d[e] == 0 && d[e + 1] == 0)) e++;
            r.SmbiosAreas.Add((type, c.off, e + 2 - c.off));
        }
        if (best1.s != null)
        {
            Add(r, "BRAND", S(best1, 4), src);
            Add(r, "MODEL", S(best1, 5), src);
            Add(r, "VERSION", S(best1, 6), src);
            Add(r, "SERIAL", S(best1, 7), src);
            if (best1.len >= 0x19)
            {
                var u = new ReadOnlySpan<byte>(d, (int)best1.off + 8, 16).ToArray();
                if (u.Any(b => b != 0) && u.Any(b => b != 0xFF)) Add(r, "UUID", new Guid(u).ToString().ToUpperInvariant(), src);
            }
            if (best1.len >= 0x1B)
            {
                Add(r, "SKU", S(best1, 0x19), src);
                Add(r, "FAMILY", S(best1, 0x1A), src);
            }
        }
        if (best2.s != null)
        {
            Add(r, "BOARD MFR", S(best2, 4), src);
            Add(r, "BOARD", S(best2, 5), src);
            Add(r, "BOARD SERIAL", S(best2, 7), src);
        }
        if (best3.s != null) Add(r, "CHASSIS SERIAL", S(best3, 7), src);
        if (best0.s != null)
        {
            r.BiosVendor = S(best0, 4);
            r.BiosVersion = S(best0, 5);
            r.BiosDate = S(best0, 8);
        }
    }

    static string[] ReadSmbiosStrings(byte[] d, int p)
    {
        if (p + 2 >= d.Length) return null;
        if (d[p] == 0 && d[p + 1] == 0) return null;
        var list = new List<string>(6);
        var sb = new StringBuilder();
        int end = Math.Min(d.Length - 1, p + 600);
        while (p < end)
        {
            byte b = d[p++];
            if (b == 0)
            {
                if (sb.Length == 0) return list.Count > 0 ? list.ToArray() : null;
                if (sb.Length < 2) return null;
                list.Add(sb.ToString());
                sb.Clear();
                if (list.Count > 16) return null;
                if (d[p] == 0) return list.ToArray();
                continue;
            }
            if (b < 0x20 || b > 0x7E || sb.Length > 64) return null;
            sb.Append((char)b);
        }
        return null;
    }

    static void Add(AnalysisResult r, string key, string value, string src)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (r.Dmi.Any(x => x.Key == key)) return;
        r.Dmi.Add(new InfoRow(key, value, src));
    }

    static readonly (string brand, Regex rx)[] ModelRx =
    {
        ("HP", new Regex(@"^HP (EliteBook|ProBook|ZBook|Pavilion|ENVY|Envy|Spectre|OMEN|EliteDesk|ProDesk|Elite|Chromebook|Stream|Laptop|Victus|Dragonfly)\b[ \w\-]{0,40}$", RegexOptions.Compiled)),
        ("LENOVO", new Regex(@"^(ThinkPad|ThinkBook|ThinkCentre|IdeaPad|Legion|Yoga)\b[ \w\-]{1,40}$", RegexOptions.Compiled)),
        ("Dell Inc.", new Regex(@"^(Latitude|Inspiron|Vostro|XPS|Precision|OptiPlex|Alienware|G15|G16)\b[ \w\-]{1,30}$", RegexOptions.Compiled)),
        ("ASUSTeK COMPUTER INC.", new Regex(@"^(ASUS (VivoBook|ZenBook|TUF|ROG|ExpertBook|Laptop)|VivoBook|ZenBook|ROG (Strix|Zephyrus|Flow))\b[ \w\-]{0,40}$", RegexOptions.Compiled)),
        ("Acer", new Regex(@"^(Aspire|Swift|Nitro|TravelMate|Predator|Extensa|Spin)\b[ \w\-]{1,30}$", RegexOptions.Compiled)),
        ("Micro-Star International", new Regex(@"^MS-\d{4,5}$", RegexOptions.Compiled)),
    };

    static readonly Regex HpSerial = new(@"^(5C[A-Z]|CN[A-Z0-9]|2C[A-Z]|MXL|CZC|8C[A-Z]|4C[A-Z]|1H[A-Z]|3C[A-Z]|5CD|5CG)[0-9][0-9A-Z]{6}$", RegexOptions.Compiled);
    static readonly Regex LenovoSerial = new(@"^(PF|PC|R9|MP|PG|S1|SL|PW|LR|MJ|PB|MZ)[0-9A-Z]{6}$", RegexOptions.Compiled);

    static void HeuristicDmi(AnalysisResult r, List<(long off, string s)> strings)
    {
        const string src = "heuristic";
        if (r.Dmi1("MODEL") == "")
        {
            var hits = new Dictionary<string, int>();
            string brand = "";
            foreach (var (_, s) in strings)
            {
                if (s.Length > 60) continue;
                foreach (var (b, rx) in ModelRx)
                {
                    if (rx.IsMatch(s))
                    {
                        hits[s] = hits.TryGetValue(s, out var c) ? c + 1 : 1;
                        if (hits[s] == 1 && brand == "") brand = b;
                        break;
                    }
                }
            }
            if (hits.Count > 0)
            {
                var best = hits.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key.Length).First().Key;
                foreach (var (b, rx) in ModelRx) if (rx.IsMatch(best)) { brand = b; break; }
                if (r.Dmi1("BRAND") == "") Add(r, "BRAND", brand == "HP" ? "HP" : brand, src);
                Add(r, "MODEL", best, src);
            }
        }
        if (r.Dmi1("SERIAL") == "")
        {
            string brand = r.Dmi1("BRAND").ToUpperInvariant();
            Regex rx = brand.StartsWith("HP") || brand.Contains("HEWLETT") ? HpSerial : brand.Contains("LENOVO") ? LenovoSerial : null;
            if (rx != null)
            {
                var s = strings.FirstOrDefault(x => x.s.Length <= 12 && rx.IsMatch(x.s));
                if (s.s != null) Add(r, "SERIAL", s.s, src);
            }
        }
    }

    // ------------------------------------------------------------------ Windows OEM key
    static readonly Regex KeyRx = new(@"^[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}$", RegexOptions.Compiled);
    static readonly Regex KeyInRx = new(@"[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}", RegexOptions.Compiled);

    static void FindOemKey(byte[] d, AnalysisResult r, List<(long off, string s)> strings)
    {
        long p = 0;
        while ((p = IndexOf(d, "MSDM"u8, p, d.Length)) >= 0)
        {
            if (U32(d, p + 4) == 0x55 && p + 0x38 + 29 <= d.Length)
            {
                var k = Encoding.ASCII.GetString(d, (int)p + 0x38, 29);
                if (KeyRx.IsMatch(k)) { r.OemKey = k; r.OemKeySource = $"ACPI MSDM table @0x{p:X}"; r.MsdmOffset = p; return; }
            }
            p += 4;
        }
        foreach (var (off, s) in strings)
        {
            var m = KeyInRx.Match(s);
            if (m.Success && m.Value.Any(char.IsLetter) && m.Value.Any(char.IsDigit))
            {
                r.OemKey = m.Value;
                r.OemKeySource = $"Firmware string @0x{off:X} (NVRAM / OA3 area)";
                return;
            }
        }
    }

    // ------------------------------------------------------------------ NVRAM
    static void ParseNvram(byte[] d, AnalysisResult r)
    {
        const int MAX = 3000;
        // VSS / authenticated VSS variables: StartId 0x55AA
        for (int i = 0; i + 64 < d.Length && r.Nvram.Count < MAX; i++)
        {
            if (d[i] != 0xAA || d[i + 1] != 0x55) continue;
            byte state = d[i + 2];
            if (state != 0x3F && state != 0x3E && state != 0x3C && state != 0x7F) continue;
            foreach (bool auth in new[] { false, true })
            {
                int hdr = auth ? 60 : 32;
                int nsOff = auth ? 36 : 8;
                uint nameSize = U32(d, i + nsOff), dataSize = U32(d, i + nsOff + 4);
                if (nameSize < 4 || nameSize > 256 || (nameSize & 1) != 0 || dataSize > 0x20000) continue;
                long nameAt = i + hdr;
                if (nameAt + nameSize + dataSize > d.Length) continue;
                if (U16(d, nameAt + nameSize - 2) != 0) continue;
                var name = Encoding.Unicode.GetString(d, (int)nameAt, (int)nameSize - 2);
                if (name.Length == 0 || !name.All(c => c >= 0x20 && c < 0x7F)) continue;
                var guid = new Guid(new ReadOnlySpan<byte>(d, i + nsOff + 8, 16));
                r.Nvram.Add(new NvramVar
                {
                    Format = auth ? "VSS (auth)" : "VSS",
                    Name = name,
                    Guid = guid.ToString().ToUpperInvariant(),
                    Offset = i,
                    DataSize = (int)dataSize,
                    Attributes = $"0x{U32(d, i + 4):X}",
                    State = state == 0x3F ? "valid" : state == 0x3E ? "deleting" : "deleted",
                    Preview = Preview(d, nameAt + nameSize, (int)dataSize),
                });
                i += hdr + (int)nameSize - 1;
                break;
            }
        }
        // AMI NVAR
        long p = 0;
        while ((p = IndexOf(d, "NVAR"u8, p, d.Length)) >= 0 && r.Nvram.Count < MAX)
        {
            int size = U16(d, p + 4);
            byte attr = d.Length > p + 9 ? d[p + 9] : (byte)0;
            if (size >= 11 && p + size <= d.Length && (attr & 0x80) != 0 && (attr & 0x08) == 0)
            {
                long q = p + 10;
                q += (attr & 0x04) != 0 ? 16 : 1;
                string name = "";
                if ((attr & 0x02) != 0)
                {
                    var sb = new StringBuilder();
                    while (q < p + size && d[q] != 0 && sb.Length < 128) sb.Append((char)d[q++]);
                    name = sb.ToString(); q++;
                }
                else
                {
                    var sb = new StringBuilder();
                    while (q + 1 < p + size && U16(d, q) != 0 && sb.Length < 128) { sb.Append((char)U16(d, q)); q += 2; }
                    name = sb.ToString(); q += 2;
                }
                if (name.Length > 0 && name.All(c => c >= 0x20 && c < 0x7F))
                {
                    int ds = (int)Math.Max(0, p + size - q);
                    r.Nvram.Add(new NvramVar
                    {
                        Format = "AMI NVAR",
                        Name = name,
                        Guid = (attr & 0x04) != 0 ? new Guid(new ReadOnlySpan<byte>(d, (int)p + 10, 16)).ToString().ToUpperInvariant() : $"GUID index {d[p + 10]}",
                        Offset = p,
                        DataSize = ds,
                        Attributes = $"0x{attr:X2}",
                        State = "valid",
                        Preview = Preview(d, q, ds),
                    });
                }
            }
            p += 4;
        }
    }

    static string Preview(byte[] d, long off, int len)
    {
        int n = Math.Min(len, 24);
        if (off + n > d.Length || n <= 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(d[off + i].ToString("X2")).Append(' ');
        if (len > n) sb.Append('…');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ vendor / version
    static readonly Regex HpVer = new(@"^[A-Z]\d{2} Ver\. \d{2}\.\d{2}\.\d{2}", RegexOptions.Compiled);
    static readonly Regex LenovoVer = new(@"^[A-Z0-9]{4}ET\d{2}[A-Z]( \(\d\.\d{2}\s?\))?$", RegexOptions.Compiled);
    static readonly Regex DateRx = new(@"^(0[1-9]|1[0-2])/(0[1-9]|[12]\d|3[01])/((19|20)\d{2})$", RegexOptions.Compiled);

    static void DetectBiosVendor(byte[] d, AnalysisResult r, List<(long off, string s)> strings)
    {
        if (r.BiosVendor == "")
        {
            (string key, string name)[] vendors =
            {
                ("American Megatrends", "American Megatrends (AMI)"), ("Insyde", "Insyde Software (InsydeH2O)"),
                ("Phoenix Technologies", "Phoenix Technologies"), ("Award Software", "Award Software"), ("coreboot", "coreboot"),
                ("Hewlett-Packard", "HP"), ("Dell Inc", "Dell Inc."), ("LENOVO", "Lenovo"),
            };
            foreach (var (key, name) in vendors)
                if (strings.Any(s => s.s.Contains(key, StringComparison.Ordinal))) { r.BiosVendor = name; break; }
        }
        if (r.BiosVersion == "")
        {
            long ib = IndexOf(d, "$IBIOSI$"u8, 0, d.Length);
            if (ib >= 0)
            {
                var sb = new StringBuilder();
                for (long q = ib + 8; q + 1 < d.Length && sb.Length < 64; q += 2)
                {
                    char c = (char)U16(d, q);
                    if (c == 0) break;
                    if (c < 0x20 || c > 0x7E) { sb.Clear(); break; }
                    sb.Append(c);
                }
                if (sb.Length > 4) r.BiosVersion = sb.ToString();
            }
        }
        if (r.BiosVersion == "")
        {
            var v = strings.FirstOrDefault(s => HpVer.IsMatch(s.s) || LenovoVer.IsMatch(s.s));
            if (v.s != null) r.BiosVersion = v.s.Trim();
        }
        if (r.BiosDate == "")
        {
            DateTime best = DateTime.MinValue;
            foreach (var (_, s) in strings)
            {
                if (s.Length != 10) continue;
                var m = DateRx.Match(s);
                if (!m.Success) continue;
                if (DateTime.TryParseExact(s, "MM/dd/yyyy", null, System.Globalization.DateTimeStyles.None, out var dt) && dt > best && dt <= DateTime.Now.AddYears(1))
                    best = dt;
            }
            if (best != DateTime.MinValue) r.BiosDate = best.ToString("MM/dd/yyyy");
        }
        if (r.BiosVendor != "") Add(r, "BIOS VENDOR", r.BiosVendor, "");
        if (r.BiosVersion != "") Add(r, "BIOS VERSION", r.BiosVersion, "");
        if (r.BiosDate != "") Add(r, "BIOS DATE", r.BiosDate, "");
    }

    // ------------------------------------------------------------------ helpers
    public static long IndexOf(byte[] d, ReadOnlySpan<byte> pat, long start, long end)
    {
        if (start < 0) start = 0;
        end = Math.Min(end, d.Length);
        if (end - start < pat.Length) return -1;
        int i = new ReadOnlySpan<byte>(d, (int)start, (int)(end - start)).IndexOf(pat);
        return i < 0 ? -1 : start + i;
    }

    static string Ascii(byte[] d, long o, int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n && o + i < d.Length; i++) { byte b = d[o + i]; if (b == 0) break; sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?'); }
        return sb.ToString();
    }

    static bool SpanBlank(byte[] d, long o, long n) => new ReadOnlySpan<byte>(d, (int)o, (int)Math.Min(n, d.Length - o)).IndexOfAnyExcept((byte)0xFF) < 0;

    // ------------------------------------------------------------------ report
    public static string BuildReport(AnalysisResult r)
    {
        var sb = new StringBuilder();
        void H(string t) { sb.AppendLine(); sb.AppendLine(t); sb.AppendLine(new string('-', t.Length)); }
        sb.AppendLine("BiosFlash Studio - Firmware Analysis Report");
        sb.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Image size: {r.ImageSize:N0} bytes ({Flash.ChipInfo.FormatSize(r.ImageSize)})");
        H("PLATFORM");
        sb.AppendLine($"Platform      : {r.Platform}");
        if (r.PlatformDetail != "") sb.AppendLine($"Detail        : {r.PlatformDetail}");
        if (r.BiosVendor != "") sb.AppendLine($"BIOS vendor   : {r.BiosVendor}");
        if (r.BiosVersion != "") sb.AppendLine($"BIOS version  : {r.BiosVersion}");
        if (r.BiosDate != "") sb.AppendLine($"BIOS date     : {r.BiosDate}");
        if (r.GbeMac != "") sb.AppendLine($"GbE MAC       : {r.GbeMac}");
        if (r.HasDescriptor)
        {
            H($"INTEL FLASH DESCRIPTOR (IFD v{r.IfdVersion})");
            sb.AppendLine($"Lock state    : {r.DescriptorLock}");
            sb.AppendLine($"{"Region",-20} {"Start",-12} {"End",-12} {"Size",-10} Host ME  State");
            foreach (var g in r.Regions)
                sb.AppendLine($"{g.Name,-20} {g.StartText,-12} {g.EndText,-12} {g.SizeText,-10} {g.HostAccess,-4} {g.MeAccess,-4} {g.State}");
        }
        if (r.MeRows.Count > 0)
        {
            H("MANAGEMENT ENGINE");
            foreach (var m in r.MeRows) sb.AppendLine($"{m.Key,-24}: {m.Value}");
        }
        if (r.Partitions.Count > 0)
        {
            H("ME FLASH PARTITION TABLE ($FPT)");
            foreach (var p in r.Partitions) sb.AppendLine($"{p.Name,-6} owner={p.Owner,-5} offset={p.OffsetText} length={p.LengthText,-22} {p.State}");
        }
        H("DMI / SYSTEM INFORMATION");
        if (r.Dmi.Count == 0) sb.AppendLine("(no DMI data found)");
        foreach (var x in r.Dmi) sb.AppendLine($"{x.Key,-15}: {x.Value}{(x.Source != "" ? $"   [{x.Source}]" : "")}");
        H("WINDOWS OEM DIGITAL KEY");
        sb.AppendLine(r.OemKey != "" ? $"{r.OemKey}   ({r.OemKeySource})" : "(no MSDM key found)");
        if (r.Nvram.Count > 0)
        {
            H($"UEFI NVRAM VARIABLES ({r.Nvram.Count})");
            foreach (var v in r.Nvram.Take(400)) sb.AppendLine($"{v.OffsetText} {v.Format,-10} {v.State,-8} {v.Name,-32} {v.DataSize,6} B  {v.Guid}");
            if (r.Nvram.Count > 400) sb.AppendLine($"... {r.Nvram.Count - 400} more");
        }
        int empty = r.BlockMap.Count(b => b == 0), data = r.BlockMap.Count(b => b == 2);
        H("USAGE MAP");
        sb.AppendLine($"Blocks ({Flash.ChipInfo.FormatSize(r.BlockSize)}): {r.BlockMap.Length}  data={data}  empty(FF)={empty}  zero={r.BlockMap.Length - empty - data}");
        if (r.Notes.Count > 0)
        {
            H("NOTES");
            foreach (var n in r.Notes) sb.AppendLine("* " + n);
        }
        sb.AppendLine();
        sb.AppendLine($"Analysis time: {r.Elapsed.TotalMilliseconds:0} ms");
        return sb.ToString();
    }
}

public static class Strings
{
    /// <summary>Extracts printable ASCII and UTF-16LE strings.</summary>
    public static List<(long off, string s)> Extract(byte[] d, int minLen, int max)
    {
        var list = new List<(long, string)>(4096);
        int runStart = -1;
        for (int i = 0; i <= d.Length; i++)
        {
            bool pr = i < d.Length && d[i] >= 0x20 && d[i] < 0x7F;
            if (pr) { if (runStart < 0) runStart = i; continue; }
            if (runStart >= 0)
            {
                int len = i - runStart;
                if (len >= minLen && len <= 256) list.Add((runStart, Encoding.ASCII.GetString(d, runStart, len)));
                runStart = -1;
                if (list.Count >= max) return list;
            }
        }
        // UTF-16LE
        int u = -1;
        for (int i = 0; i + 1 < d.Length; i += 2)
        {
            bool pr = d[i + 1] == 0 && d[i] >= 0x20 && d[i] < 0x7F;
            if (pr) { if (u < 0) u = i; continue; }
            if (u >= 0)
            {
                int chars = (i - u) / 2;
                if (chars >= minLen && chars <= 256) list.Add((u, Encoding.Unicode.GetString(d, u, chars * 2)));
                u = -1;
                if (list.Count >= max) break;
            }
        }
        // odd-aligned UTF-16 is skipped for speed
        return list;
    }
}
