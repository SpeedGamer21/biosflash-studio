using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BiosFlashStudio.Analysis;
using BiosFlashStudio.Core;
using BiosFlashStudio.Ui;

namespace BiosFlashStudio;

public partial class MainWindow
{
    AnalysisResult _analysis;
    int _analysisGen;

    async Task AnalyzeBufferAsync()
    {
        if (_buffer.Length == 0) return;
        int gen = ++_analysisGen;
        var data = _buffer;
        Log("BIOSINTEL", "Analyzing firmware image…");
        AnalysisResult r;
        try { r = await Task.Run(() => BiosAnalyzer.Analyze(data)); }
        catch (Exception ex) { Log("BIOSINTEL", "Analysis failed: " + ex.Message, LogLevel.Error); return; }
        if (gen != _analysisGen || data != _buffer) return;
        ShowAnalysis(r);
        var model = r.Dmi1("MODEL");
        Log("BIOSINTEL", $"BIOS analyzed: Platform={r.Platform}{(model != "" ? $", Model='{model}'" : "")}{(r.Me.Present ? $", ME {r.Me.Version}" : "")} ({r.Elapsed.TotalMilliseconds:0} ms)", LogLevel.Ok);
        if (r.OemKey != "") Log("BIOSINTEL", "Windows OEM key found (" + r.OemKeySource + ")", LogLevel.Ok);
        if (r.HasDescriptor) Log("BIOSINTEL", $"Intel descriptor v{r.IfdVersion}: {r.Regions.Count} regions, {r.DescriptorLock}");
        foreach (var n in r.Notes) Log("BIOSINTEL", n, LogLevel.Warn);
    }

    void ShowAnalysis(AnalysisResult r)
    {
        _analysis = r;
        bool has = r != null;
        IntelPlatform.Text = has ? r.Platform : "—";
        IntelMe.Text = has && r.Me.Present ? $"{r.Me.Version} ({r.Me.Family})" : has && r.MeRows.Count > 0 ? "ME region present (version unknown)" : "—";
        IntelVendor.Text = has && r.BiosVendor != "" ? r.BiosVendor : "—";
        IntelBiosVer.Text = has && r.BiosVersion != "" ? r.BiosVersion + (r.BiosDate != "" ? $"  ({r.BiosDate})" : "") : has && r.BiosDate != "" ? r.BiosDate : "—";
        IntelIfd.Text = has && r.HasDescriptor ? $"v{r.IfdVersion} · {r.Regions.Count} regions · {r.DescriptorLock}" : has ? "none" : "—";
        IntelPill.IsVisible = has && r.Platform != "Unknown";
        IntelPillText.Text = has && r.Platform.StartsWith("AMD") ? "AMD" : "INTEL";
        foreach (var tb in new[] { IntelPlatform, IntelMe, IntelVendor, IntelBiosVer, IntelIfd }) ToolTip.SetTip(tb, tb.Text);

        DmiList.ItemsSource = has ? r.Dmi : null;
        DmiCount.Text = $"{(has ? r.Dmi.Count : 0)} FIELDS";
        OemKeyText.Text = has && r.OemKey != "" ? r.OemKey : "— not found —";
        OemKeySrc.Text = has && r.OemKey != "" ? r.OemKeySource : "ACPI MSDM table / NVRAM";
        MacText.Text = has && r.GbeMac != "" ? r.GbeMac : "—";

        RegionBarCtl.SetRegions(r?.Regions, r?.ImageSize ?? 0);
        RegionBarOv.SetRegions(r?.Regions, r?.ImageSize ?? 0);
        OvStat1.Text = has ? $"{Flash.ChipInfo.FormatSize(r.ImageSize)} image" : "";
        OvStat2.Text = has ? $"{r.Nvram.Count} NVRAM variables" : "";
        OvStat3.Text = has ? $"{r.Partitions.Count} ME partitions" : "";
        RegionGrid.ItemsSource = r?.Regions;
        IfdTitle.Text = has && r.HasDescriptor ? $"DESCRIPTOR REGIONS  ·  IFD v{r.IfdVersion}  ·  {r.DescriptorLock}" : "DESCRIPTOR REGIONS";
        FptGrid.ItemsSource = r?.Partitions;
        MeTable.ItemsSource = r?.MeRows;
        MeEmpty.IsVisible = !(has && r.MeRows.Count > 0);
        BlockMapCtl.SetMap(r?.BlockMap, r?.BlockSize ?? 65536, r?.Regions);
        if (has)
        {
            int empty = r.BlockMap.Count(b => b == 0), data = r.BlockMap.Count(b => b == 2);
            BlockLegend.Text = $"{r.BlockMap.Length} × {Flash.ChipInfo.FormatSize(r.BlockSize)} · data {data} · empty {empty} · zero {r.BlockMap.Length - data - empty}   (grey = 0xFF, colour = region)";
        }
        else BlockLegend.Text = "";
        NvramFilter_TextChanged(null, null);
        ReportBox.Text = r?.Report ?? "Read a chip or open an image to analyze it.";

        var items = new List<ComboBoxItem> { new() { Content = "(custom range)" } };
        if (has)
            foreach (var g in r.Regions)
                items.Add(new ComboBoxItem { Content = $"{g.Name}  {g.StartText}-{g.EndText}", Tag = g });
        RegionPick.ItemsSource = items;
        RegionPick.SelectedIndex = 0;
        RepUpdateInfo();
        UpdateHexStatus();
    }

    void NvramFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_analysis == null) { NvramGrid.ItemsSource = null; NvramTitle.Text = "UEFI NVRAM VARIABLES"; return; }
        var q = NvramFilter.Text?.Trim() ?? "";
        var list = q.Length == 0 ? _analysis.Nvram : _analysis.Nvram.Where(v => v.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || v.Guid.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        NvramGrid.ItemsSource = list;
        NvramTitle.Text = $"UEFI NVRAM VARIABLES ({_analysis.Nvram.Count})";
    }

    void NvramGrid_DoubleTapped(object sender, TappedEventArgs e)
    {
        if (NvramGrid.SelectedItem is not NvramVar v || _buffer.Length == 0) return;
        ShowPage(1);
        Hex.Select(v.Offset, Math.Max(1, v.DataSize + 32));
    }

    async void Reanalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_buffer.Length == 0) { Log("BIOSINTEL", "Nothing to analyze — buffer is empty.", LogLevel.Warn); return; }
        await AnalyzeBufferAsync();
    }

    void ShowAnalysis_Click(object sender, RoutedEventArgs e) => ShowPage(2);

    async void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis?.OemKey is { Length: > 0 } k)
        {
            try
            {
                if (Clipboard != null) await Clipboard.SetTextAsync(k);
                Log("BIOSINTEL", "OEM key copied to clipboard.", LogLevel.Ok);
            }
            catch { }
        }
        else Log("BIOSINTEL", "No OEM key to copy.", LogLevel.Warn);
    }

    async void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis == null) { Log("BIOSINTEL", "No analysis available — read or open an image first.", LogLevel.Warn); return; }
        var name = _analysis.Dmi1("MODEL");
        var serial = _analysis.Dmi1("SERIAL");
        var fn = string.Join("_", new[] { name, serial, "bios_report" }.Where(s => s != "")).Replace(' ', '_');
        foreach (var ch in Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' })) fn = fn.Replace(ch, '_');
        var p = await PickSaveAsync("Export report", fn + ".txt", Ft("Text report", "*.txt"));
        if (p == null) return;
        var header = $"Source: {_bufName}  ({_buffer.Length:N0} bytes, CRC32 0x{Hashes.Crc32(_buffer):X8}, SHA-256 {Hashes.Sha256(_buffer)})\n" +
                     (_chip != null ? $"Chip  : {_chip.Display}\n" : "") + "\n";
        await File.WriteAllTextAsync(p, header + _analysis.Report);
        Log("BIOSINTEL", "Report exported to " + p, LogLevel.Ok);
    }

    async Task<FlashRegion> PickRegionAsync()
    {
        if (RegionGrid.SelectedItem is FlashRegion r) return r;
        ShowPage(2);
        await InfoAsync("Region", "Select a region in the DESCRIPTOR REGIONS table first.");
        return null;
    }

    async void ExtractRegion_Click(object sender, RoutedEventArgs e)
    {
        var r = await PickRegionAsync();
        if (r == null) return;
        if (r.End >= _buffer.Length) { Log("REGION", "Region lies outside the buffer.", LogLevel.Error); return; }
        var p = await PickSaveAsync("Extract region", $"region_{r.Name.Replace(' ', '_')}_{r.StartText}.bin", FtBin);
        if (p == null) return;
        await File.WriteAllBytesAsync(p, _buffer[(int)r.Start..(int)(r.End + 1)]);
        Log("REGION", $"Extracted {r.Name} ({r.SizeText}) to {p}", LogLevel.Ok);
    }

    async void ReplaceRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var r = await PickRegionAsync();
        if (r == null) return;
        var path = await PickOpenAsync($"Replace {r.Name} region", Ft("Binary", "*.bin", "*.rom"), FtAll);
        if (path == null) return;
        var src = await File.ReadAllBytesAsync(path);
        if (src.Length > r.Size)
        {
            await InfoAsync("Replace region", $"File ({src.Length:N0} B) is larger than the {r.Name} region ({r.Size:N0} B).", DialogKind.Error);
            return;
        }
        if (!await AskAsync("Replace region", $"Replace {r.Name} ({r.StartText}-{r.EndText}) in the BUFFER with {Path.GetFileName(path)} ({src.Length:N0} B)?" +
                                              (src.Length < r.Size ? "\nThe remainder of the region will be filled with 0xFF." : "") +
                                              "\n\nThe chip is not touched until you program it.")) return;
        Buffer.BlockCopy(src, 0, _buffer, (int)r.Start, src.Length);
        if (src.Length < r.Size) Array.Fill(_buffer, (byte)0xFF, (int)r.Start + src.Length, (int)r.Size - src.Length);
        Hex.RefreshView();
        _modCount = Hex.ModifiedCount();
        UpdateBufferInfo();
        Log("REGION", $"{r.Name} region replaced in buffer from {Path.GetFileName(path)}.", LogLevel.Ok);
        await AnalyzeBufferAsync();
    }

    async void UnlockIfd_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _buffer.Length == 0) return;
        if (!await AskAsync("Unlock descriptor", "Grant the CPU/BIOS, ME and GbE masters full read/write access in the flash descriptor (FLMSTR1-3)?\n\n" +
                                                 "This lets software tools (e.g. FPT) rewrite all regions. The change is made in the BUFFER only.", DialogKind.Warning, "Unlock", "Cancel")) return;
        Snapshot("descriptor unlock");
        if (!BiosAnalyzer.UnlockDescriptor(_buffer)) { Log("REGION", "No Intel flash descriptor found in buffer.", LogLevel.Error); return; }
        Hex.RefreshView();
        _modCount = Hex.ModifiedCount();
        UpdateBufferInfo();
        Log("REGION", "Flash descriptor unlocked in buffer (FLMSTR1-3 = full access).", LogLevel.Ok);
        await AnalyzeBufferAsync();
    }
}
