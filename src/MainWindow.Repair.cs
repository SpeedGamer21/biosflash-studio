using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BiosFlashStudio.Analysis;
using BiosFlashStudio.Core;
using BiosFlashStudio.Flash;
using BiosFlashStudio.Ui;

namespace BiosFlashStudio;

/// <summary>Repair Tools and Update Extractor pages.</summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ state
    byte[] _stock;
    string _stockName = "";
    AnalysisResult _stockA;
    List<MergeRegion> _repPlan;
    List<IdentityItem> _repIds;
    List<NvramStore> _repStores = new();
    FirmwareRepair.MeCleanPlan _repMe;

    byte[] _undo;
    string _undoName, _undoSource, _undoWhat;

    ExtractResult _ext;

    // ================================================================== undo
    void Snapshot(string what)
    {
        if (_buffer.Length == 0) return;
        _undo = (byte[])_buffer.Clone();
        _undoName = _bufName;
        _undoSource = _bufSource;
        _undoWhat = what;
        RepUndoBtn.IsEnabled = true;
        ToolTip.SetTip(RepUndoBtn, "Undo: " + what);
    }

    async void RepUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo == null || _busy) return;
        if (!await AskAsync("Undo", $"Restore the buffer as it was before '{_undoWhat}'?")) return;
        SetBuffer(_undo, _undoName, null, _undoSource, true);
        Log("REPAIR", $"Undone: {_undoWhat}.", LogLevel.Warn);
        _undo = null;
        RepUndoBtn.IsEnabled = false;
    }

    /// <summary>Call after the buffer was modified in place.</summary>
    async Task AfterInPlaceEdit()
    {
        Hex.RefreshView();
        _modCount = Hex.ModifiedCount();
        UpdateBufferInfo();
        UpdateHexStatus();
        await AnalyzeBufferAsync();
    }

    bool NeedBuffer()
    {
        if (_buffer.Length > 0) return true;
        Log("REPAIR", "The buffer is empty — read the chip or open a dump first.", LogLevel.Warn);
        return false;
    }

    // ================================================================== quick fixes
    void RepRefresh_Click(object sender, RoutedEventArgs e) => RepUpdateInfo();

    /// <summary>Refreshes the quick-fix panel from the current buffer and analysis.</summary>
    void RepUpdateInfo()
    {
        if (RepNvramInfo == null) return;
        if (_buffer.Length == 0)
        {
            RepNvramInfo.Text = RepMeInfo.Text = RepMacInfo.Text = RepFdInfo.Text = "Read the chip or open a dump first.";
            RepMeBtn.IsEnabled = false;
            _repStores = new();
            return;
        }
        _repStores = FirmwareRepair.FindNvramStores(_buffer);
        RepNvramInfo.Text = _repStores.Count == 0
            ? "No UEFI variable store found (VSS / AMI NVAR)."
            : string.Join("\n", _repStores.Select(s => s.ToString())) + "\nResets BIOS setup to defaults; keep the original dump.";

        _repMe = FirmwareRepair.PlanMeClean(_buffer, _analysis);
        RepMeInfo.Text = _repMe.Reason;
        RepMeBtn.IsEnabled = _repMe.Allowed;

        var gbe = _analysis?.Regions.FirstOrDefault(r => r.Index == 3);
        if (gbe == null) RepMacInfo.Text = "No GbE region in this image (MAC is stored elsewhere, e.g. in the LAN chip's own EEPROM).";
        else
        {
            var banks = FirmwareRepair.GbeBanks(_buffer, gbe);
            var mac = FirmwareRepair.ReadMac(_buffer, gbe);
            if (string.IsNullOrWhiteSpace(RepMacBox.Text)) RepMacBox.Text = mac;
            RepMacInfo.Text = banks.Count == 0
                ? $"GbE region @0x{gbe.Start:X} has no valid Intel NVM bank — MAC cannot be set safely."
                : $"Current MAC {mac} · {banks.Count} NVM bank(s), checksum {(banks.All(b => FirmwareRepair.GbeChecksumOk(_buffer, b)) ? "OK" : "BAD — Set MAC fixes it")}";
        }
        RepFdInfo.Text = _analysis?.HasDescriptor == true
            ? $"IFD v{_analysis.IfdVersion} · {_analysis.DescriptorLock}. Unlocking lets software flashers write every region."
            : "No Intel flash descriptor. Use Region merge → 'Repair descriptor' with a stock image.";
    }

    async void RepClearNvram_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !NeedBuffer()) return;
        if (_repStores.Count == 0) { Log("REPAIR", "No NVRAM store found.", LogLevel.Warn); return; }
        int vars = _repStores.Sum(s => s.Variables);
        if (!await AskAsync("Clear NVRAM",
                $"Erase {vars} variable(s) in {_repStores.Count} store(s)?\n\n{string.Join("\n", _repStores)}\n\n" +
                "BIOS settings, boot entries and anything vendor tools saved in NVRAM are reset to defaults. Only the BUFFER changes.",
                DialogKind.Warning, "Clear", "Cancel")) return;
        Snapshot("clear NVRAM");
        long n = FirmwareRepair.ClearNvram(_buffer, _repStores);
        Log("REPAIR", $"NVRAM cleared: {vars} variable(s), {ChipInfo.FormatSize(n)} erased in {_repStores.Count} store(s).", LogLevel.Ok);
        await AfterInPlaceEdit();
    }

    async void RepCleanMe_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !NeedBuffer() || _repMe == null || !_repMe.Allowed) return;
        if (!await AskAsync("Clean ME region", _repMe.Reason + "\n\nOnly the BUFFER changes. Program it afterwards and let the board power on once with AC connected.",
                DialogKind.Warning, "Clean", "Cancel")) return;
        Snapshot("clean ME");
        FirmwareRepair.ApplyMeClean(_buffer, _repMe);
        Log("REPAIR", $"ME cleaned: {string.Join(", ", _repMe.Partitions.Select(p => $"{p.name} @0x{p.abs:X}"))} wiped.", LogLevel.Ok);
        await AfterInPlaceEdit();
    }

    async void RepSetMac_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !NeedBuffer()) return;
        var gbe = _analysis?.Regions.FirstOrDefault(r => r.Index == 3);
        if (gbe == null || FirmwareRepair.GbeBanks(_buffer, gbe).Count == 0) { Log("REPAIR", "No valid GbE NVM in the buffer.", LogLevel.Warn); return; }
        var mac = FirmwareRepair.ParseMac(RepMacBox.Text);
        if (mac == null)
        {
            await InfoAsync("MAC address", "Enter 12 hex digits, e.g. 3C:52:82:1A:2B:3C. Multicast, all-zero and broadcast addresses are not allowed.", DialogKind.Warning);
            return;
        }
        var text = string.Join(":", mac.Select(b => b.ToString("X2")));
        if (!await AskAsync("Set MAC address", $"Write MAC {text} to the GbE region and fix its checksum?\n\nUse the address printed on the board / laptop label.")) return;
        Snapshot("set MAC");
        int banks = FirmwareRepair.SetMac(_buffer, gbe, mac);
        Log("REPAIR", $"MAC set to {text} in {banks} GbE NVM bank(s), checksum updated.", LogLevel.Ok);
        RepMacBox.Text = text;
        await AfterInPlaceEdit();
    }

    // ================================================================== binary tools
    bool BinRange(out long off, out long len)
    {
        off = len = 0;
        if (!NeedBuffer()) return false;
        if (!TryParseNum(BinOffset.Text, out off) || !TryParseNum(BinLength.Text, out len) || len <= 0 || off < 0 || off + len > _buffer.Length)
        {
            Log("BINARY", $"Invalid range (buffer is 0x{_buffer.Length:X} bytes).", LogLevel.Warn);
            return false;
        }
        return true;
    }

    async void BinExtract_Click(object sender, RoutedEventArgs e)
    {
        if (!BinRange(out var off, out var len)) return;
        var p = await PickSaveAsync("Extract range", $"{Path.GetFileNameWithoutExtension(_bufName)}_0x{off:X}_0x{len:X}.bin", FtBin, FtAll);
        if (p == null) return;
        await File.WriteAllBytesAsync(p, _buffer.AsSpan((int)off, (int)len).ToArray());
        Log("BINARY", $"Saved 0x{len:X} bytes from 0x{off:X} to {p}", LogLevel.Ok);
    }

    async void BinSplit_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedBuffer()) return;
        if (!TryParseNum(BinOffset.Text, out var off) || off <= 0 || off >= _buffer.Length) { Log("BINARY", "Enter a split offset inside the buffer.", LogLevel.Warn); return; }
        var b = Path.GetFileNameWithoutExtension(_bufName == "" ? "image" : _bufName);
        var p1 = await PickSaveAsync("Save part 1 (start … offset)", $"{b}_part1_0x0.bin", FtBin, FtAll);
        if (p1 == null) return;
        var p2 = await PickSaveAsync("Save part 2 (offset … end)", $"{b}_part2_0x{off:X}.bin", FtBin, FtAll);
        if (p2 == null) return;
        await File.WriteAllBytesAsync(p1, _buffer.AsSpan(0, (int)off).ToArray());
        await File.WriteAllBytesAsync(p2, _buffer.AsSpan((int)off).ToArray());
        Log("BINARY", $"Split at 0x{off:X}: {ChipInfo.FormatSize(off)} → {Path.GetFileName(p1)}, {ChipInfo.FormatSize(_buffer.Length - off)} → {Path.GetFileName(p2)}", LogLevel.Ok);
    }

    async void BinResize_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !NeedBuffer()) return;
        long size;
        var tag = (BinSize.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag == "chip") size = _chip?.Size ?? 0;
        else if (tag == "offset") { if (!TryParseNum(BinOffset.Text, out size)) size = 0; }
        else size = long.TryParse(tag, out var s) ? s : 0;
        if (size <= 0 || size > 256L * 1024 * 1024) { Log("BINARY", "Pick a valid target size.", LogLevel.Warn); return; }
        if (size == _buffer.Length) { Log("BINARY", "The buffer already has that size."); return; }
        if (!await AskAsync("Resize buffer", size > _buffer.Length
                ? $"Pad the buffer from {ChipInfo.FormatSize(_buffer.Length)} to {ChipInfo.FormatSize(size)} with 0xFF?"
                : $"Truncate the buffer from {ChipInfo.FormatSize(_buffer.Length)} to {ChipInfo.FormatSize(size)}? Data after 0x{size:X} is dropped.",
                size > _buffer.Length ? DialogKind.Question : DialogKind.Warning)) return;
        Snapshot("resize");
        SetBuffer(FirmwareRepair.Resize(_buffer, size), _bufName, null, "Resized", true);
        Log("BINARY", $"Buffer resized to {ChipInfo.FormatSize(size)}.", LogLevel.Ok);
    }

    async void BinAppend_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !NeedBuffer()) return;
        var p = await PickOpenAsync("Append file to the buffer", FtFirmware, FtAll);
        if (p == null) return;
        var add = await File.ReadAllBytesAsync(p);
        Snapshot("append file");
        var joined = new byte[_buffer.Length + add.Length];
        Buffer.BlockCopy(_buffer, 0, joined, 0, _buffer.Length);
        Buffer.BlockCopy(add, 0, joined, _buffer.Length, add.Length);
        SetBuffer(joined, _bufName, null, "Joined", true);
        Log("BINARY", $"Appended {Path.GetFileName(p)} ({ChipInfo.FormatSize(add.Length)}) at 0x{joined.Length - add.Length:X}; buffer is now {ChipInfo.FormatSize(joined.Length)}.", LogLevel.Ok);
    }

    // ================================================================== stock image + merge
    async void RepLoadStock_Click(object sender, RoutedEventArgs e)
    {
        var p = await PickOpenAsync("Load stock firmware / BIOS update", FtFirmware, Ft("Updates & archives", "*.exe", "*.zip", "*.fl1", "*.fl2", "*.FL1"), FtAll);
        if (p != null) await RepLoadStockFileAsync(p);
    }

    async Task RepLoadStockFileAsync(string path)
    {
        try
        {
            var raw = await File.ReadAllBytesAsync(path);
            var ex = await Task.Run(() => UpdateExtractor.Scan(raw, Path.GetFileName(path)));
            foreach (var n in ex.Notes) Log("REPAIR", n, LogLevel.Warn);
            if (ex.Candidates.Count == 0) { RepStockInfo.Text = "No firmware image found in " + Path.GetFileName(path); return; }
            long want = _buffer.Length > 0 ? _buffer.Length : _chip?.Size ?? 0;
            var pick = ex.Candidates.FirstOrDefault(c => c.Length == want && c.Score >= 50) ?? ex.Candidates[0];
            await SetStockAsync(pick, Path.GetFileName(path));
        }
        catch (Exception ex) { Log("REPAIR", "Could not load stock image: " + ex.Message, LogLevel.Error); }
    }

    async Task SetStockAsync(ExtractCandidate c, string name)
    {
        var data = c.Data;
        var bios = _analysis?.Regions.FirstOrDefault(r => r.Index == 1);
        // A BIOS-region-only image (UEFI volumes / Insyde BIOS block) is placed into a copy of the dump.
        if (data.Length != _buffer.Length && bios != null && data.Length == bios.Size)
        {
            var full = (byte[])_buffer.Clone();
            Buffer.BlockCopy(data, 0, full, (int)bios.Start, data.Length);
            data = full;
            Log("REPAIR", $"Stock image holds only the BIOS region ({ChipInfo.FormatSize(c.Length)}) — placed into a copy of your dump at 0x{bios.Start:X}.", LogLevel.Accent);
        }
        _stock = data;
        _stockName = $"{name} [{c.Kind}]";
        _stockA = await Task.Run(() => BiosAnalyzer.Analyze(_stock));
        RepStockInfo.Text = $"{_stockName}\n{FirmwareRepair.Describe(_stock, _stockA)}\nCRC32 0x{Hashes.Crc32(_stock):X8}";
        Log("REPAIR", $"Stock image loaded: {_stockName} — {FirmwareRepair.Describe(_stock, _stockA)}", LogLevel.Ok);
        ShowPage(4);
        RepPlan();
    }

    async void RepUseCap_Click(object sender, RoutedEventArgs e)
    {
        if (_cap == null || _cap.BodyLength == 0) { Log("REPAIR", "Open a .CAP in the ASUS CAP Tool first.", LogLevel.Warn); return; }
        await SetStockAsync(new ExtractCandidate { Kind = _cap.Kind, Data = _cap.Body, Score = 80 }, _cap.FileName);
    }

    void RepPlan_Click(object sender, RoutedEventArgs e) => RepPlan();

    void RepPlan()
    {
        _repPlan = null;
        _repIds = null;
        RepMergeGrid.ItemsSource = null;
        RepIdGrid.ItemsSource = null;
        if (_stock == null) { RepResultInfo.Text = "Load a stock image first."; return; }
        if (_buffer.Length == 0) { RepResultInfo.Text = "Read the chip (or open the original dump) so the buffer holds it."; return; }
        if (_buffer.Length != _stock.Length)
        {
            RepResultInfo.Text = $"Size mismatch: dump {ChipInfo.FormatSize(_buffer.Length)} vs stock {ChipInfo.FormatSize(_stock.Length)}.";
            Log("REPAIR", RepResultInfo.Text, LogLevel.Warn);
            return;
        }
        _repPlan = FirmwareRepair.PlanMerge(_stock, _stockA, _buffer, _analysis);
        _repIds = FirmwareRepair.PlanIdentity(_buffer, _analysis, _stock, _stockA);
        RepMergeGrid.ItemsSource = _repPlan;
        RepIdGrid.ItemsSource = _repIds;
        long diff = 0;
        for (int i = 0; i < _stock.Length; i++) if (_stock[i] != _buffer[i]) diff++;
        RepResultInfo.Text = _repPlan.Count == 0
            ? $"{diff:N0} bytes differ. Neither image has an Intel descriptor, so the whole stock image is used; identity items are still transferred."
            : $"{diff:N0} bytes differ. Pick a preset or tick regions, check the identity list, then build.";
        Log("REPAIR", $"Repair plan: {_repPlan.Count} regions, {_repIds.Count(i => i.CanTransfer)} transferable identity item(s), {diff:N0} differing bytes.", LogLevel.Accent);
    }

    void RepPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_repPlan == null || _repPlan.Count == 0) { Log("REPAIR", "Build a repair plan first (load a stock image).", LogLevel.Warn); return; }
        var tag = (sender as Button)?.Tag as string;
        foreach (var r in _repPlan)
        {
            var n = r.Name.ToUpperInvariant();
            r.KeepFromDump = tag switch
            {
                "bios" => !n.Contains("BIOS"),
                "fd" => !n.Contains("DESCRIPTOR"),
                "me" => !(n.Contains("ME") && !n.Contains("DESCRIPTOR")),
                _ => n.Contains("GBE") || n == "EC" || n.Contains("PDR") || n.Contains("PLATFORM"),
            };
        }
        RepMergeGrid.ItemsSource = null;
        RepMergeGrid.ItemsSource = _repPlan;
        var fromStock = _repPlan.Where(r => !r.KeepFromDump).Select(r => r.Name);
        RepResultInfo.Text = $"From stock: {string.Join(", ", fromStock)}. Everything else stays from your dump.";
    }

    async void RepBuild_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _stock == null || _repIds == null || _repPlan == null) { Log("REPAIR", "Load a stock image and compare with the buffer first.", LogLevel.Warn); return; }
        if (_buffer.Length != _stock.Length) { RepPlan(); return; }
        var fromStock = _repPlan.Count == 0 ? new List<string> { "whole image" } : _repPlan.Where(r => !r.KeepFromDump).Select(r => r.Name).ToList();
        var ids = _repIds.Where(i => i.Transfer && i.CanTransfer).Select(i => i.Kind).ToList();
        if (fromStock.Count == 0 && ids.Count == 0) { Log("REPAIR", "Nothing selected — every region is kept from the dump.", LogLevel.Warn); return; }
        if (!await AskAsync("Build repaired image",
                $"From the stock image: {string.Join(", ", fromStock)}\n" +
                $"Kept from your dump: {(_repPlan.Any(r => r.KeepFromDump) ? string.Join(", ", _repPlan.Where(r => r.KeepFromDump).Select(r => r.Name)) : "—")}\n" +
                $"Identity transferred: {(ids.Count > 0 ? string.Join(", ", ids) : "—")}\n\n" +
                "The result replaces the buffer (Undo is available). Save the original dump before programming.", DialogKind.Question, "Build", "Cancel")) return;
        Snapshot("repair merge");
        var dump = _buffer;
        var merged = _repPlan.Count == 0 ? (byte[])_stock.Clone() : FirmwareRepair.Merge(_stock, dump, _repPlan, out _);
        var mergedA = await Task.Run(() => BiosAnalyzer.Analyze(merged));
        var done = FirmwareRepair.ApplyIdentity(merged, dump, mergedA, _repIds);
        var name = $"{Path.GetFileNameWithoutExtension(_bufName == "" ? "image" : _bufName)}_repaired.bin";
        SetBuffer(merged, name, null, "Repaired", true);
        Log("REPAIR", $"Repaired image built — stock: {string.Join(", ", fromStock)}; identity: {(done.Count > 0 ? string.Join(", ", done) : "none")}. CRC32 0x{Hashes.Crc32(merged):X8}", LogLevel.Ok);
        RepResultInfo.Text = "Repaired image is in the buffer. Check the Overview (MAC, serial, ME), save it, then program with Auto Program (F8).";
        RepMergeGrid.ItemsSource = null;
        RepIdGrid.ItemsSource = null;
        _repPlan = null;
        _repIds = null;
        SetStatus("Repaired image in buffer — review, then Auto Program (F8)");
    }

    // ================================================================== update extractor
    async void ExtOpen_Click(object sender, RoutedEventArgs e)
    {
        var p = await PickOpenAsync("Open vendor BIOS update", Ft("BIOS updates", "*.exe", "*.zip", "*.cap", "*.CAP", "*.bin", "*.rom", "*.fd", "*.fl1", "*.fl2", "*.FL1", "*.upd", "*.img"), FtAll);
        if (p != null) await ExtOpenFileAsync(p);
    }

    async Task ExtOpenFileAsync(string path)
    {
        try
        {
            ShowPage(5);
            ExtTitle.Text = "Scanning " + Path.GetFileName(path) + "…";
            var raw = await File.ReadAllBytesAsync(path);
            _ext = await Task.Run(() => UpdateExtractor.Scan(raw, Path.GetFileName(path)));
            ExtTitle.Text = $"{Path.GetFileName(path)}  ·  {ChipInfo.FormatSize(raw.Length)}  ·  {_ext.Candidates.Count} image(s) found";
            ExtGrid.ItemsSource = _ext.Candidates;
            ExtNotes.ItemsSource = _ext.Notes;
            if (_ext.Candidates.Count > 0) ExtGrid.SelectedIndex = 0;
            else ExtSelInfo.Text = "Nothing extractable — see the notes above.";
            Log("EXTRACT", $"{Path.GetFileName(path)}: {_ext.Candidates.Count} image(s): {string.Join("; ", _ext.Candidates.Take(5).Select(c => $"{c.Kind} {c.SizeText}"))}", _ext.Candidates.Count > 0 ? LogLevel.Ok : LogLevel.Warn);
            foreach (var n in _ext.Notes) Log("EXTRACT", n, LogLevel.Warn);
        }
        catch (Exception ex) { Log("EXTRACT", "Could not scan file: " + ex.Message, LogLevel.Error); }
    }

    ExtractCandidate ExtSelected => ExtGrid.SelectedItem as ExtractCandidate;

    async void ExtGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var c = ExtSelected;
        if (c == null) { ExtSelInfo.Text = "Select an image above."; return; }
        ExtSelInfo.Text = $"{c.Kind} · {c.SizeText} · from {c.Source}";
        if (c.Length < 0x10000) return;
        var a = await Task.Run(() => BiosAnalyzer.Analyze(c.Data));
        if (ExtSelected != c) return;
        string sizeNote = _chip == null ? "" : c.Length == _chip.Size ? $"\n✓ Matches the selected chip ({_chip.Name}, {_chip.SizeText})."
            : $"\n⚠ Size differs from the selected chip ({_chip.Name}, {_chip.SizeText})" + (c.Kind.Contains("BIOS region") || c.Kind.StartsWith("InsydeFlash BIOS") ? " — use 'Use as stock image' to place it into your dump's BIOS region." : ".");
        ExtSelInfo.Text = $"{c.Kind} · {c.SizeText} · CRC32 0x{c.Crc} · from {c.Source}\n{FirmwareRepair.Describe(c.Data, a)}" +
                          (a.Dmi1("MODEL") != "" ? $" · {a.Dmi1("MODEL")}" : "") + sizeNote;
    }

    void ExtGrid_DoubleTapped(object sender, TappedEventArgs e) => ExtLoad_Click(null, null);

    async void ExtLoad_Click(object sender, RoutedEventArgs e)
    {
        var c = ExtSelected;
        if (c == null || _busy) return;
        if (_buffer.Length > 0 && _bufSource == "Chip read" &&
            !await AskAsync("Replace buffer", "The buffer holds a chip read. Replace it with the extracted image?\n\nTip: 'Use as stock image' keeps your dump and lets you merge.", DialogKind.Warning, "Replace", "Cancel"))
            return;
        SetBuffer((byte[])c.Data.Clone(), $"{Path.GetFileNameWithoutExtension(_ext.FileName)}_{c.Kind.Split(' ')[0].ToLowerInvariant()}_0x{c.Offset:X}.bin", null, "Extracted", true);
        Log("EXTRACT", $"Loaded {c.Kind} ({c.SizeText}, CRC32 0x{c.Crc}) into the buffer.", LogLevel.Ok);
    }

    async void ExtSave_Click(object sender, RoutedEventArgs e)
    {
        var c = ExtSelected;
        if (c == null) return;
        var ext = c.Kind.Contains("platform.ini") ? ".ini" : ".bin";
        var p = await PickSaveAsync("Save extracted image", $"{Path.GetFileNameWithoutExtension(_ext.FileName)}_0x{c.Offset:X}{ext}", FtBin, FtAll);
        if (p == null) return;
        await File.WriteAllBytesAsync(p, c.Data);
        Log("EXTRACT", $"Saved {c.Kind} ({c.SizeText}) to {p}", LogLevel.Ok);
    }

    async void ExtUseStock_Click(object sender, RoutedEventArgs e)
    {
        var c = ExtSelected;
        if (c == null) return;
        await SetStockAsync(c, _ext.FileName);
    }
}
