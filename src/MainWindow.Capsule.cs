using Avalonia.Controls;
using Avalonia.Interactivity;
using BiosFlashStudio.Analysis;
using BiosFlashStudio.Core;
using BiosFlashStudio.Flash;
using BiosFlashStudio.Ui;

namespace BiosFlashStudio;

public partial class MainWindow
{
    CapsuleInfo _cap;
    List<MergeRegion> _capPlan;

    void ShowCapsule(CapsuleInfo cap, byte[] raw = null)
    {
        _cap = cap;
        _capPlan = null;
        CapMergeGrid.ItemsSource = null;
        if (cap == null)
        {
            CapTitle.Text = "No capsule loaded";
            CapRows.ItemsSource = null;
            CapWarnings.ItemsSource = null;
            SetPill(CapPill, CapPillText, "—", "TxtDim");
            CapSizeCheck.Text = "Open a .CAP file to begin.";
            CapSizeCheck.Res(TextBlock.ForegroundProperty, "TxtDim");
            CapMergeInfo.Text = "Load a capsule and read/open the original dump to plan a merge.";
            return;
        }
        CapTitle.Text = (cap.BoardModel != "" ? cap.BoardModel : cap.FileName) + (cap.BiosVersion != "" ? $"  ·  BIOS {cap.BiosVersion}" : "");
        CapRows.ItemsSource = cap.Rows;
        CapWarnings.ItemsSource = cap.Warnings;
        SetPill(CapPill, CapPillText, cap.IsCapsule ? (cap.IsAsus ? "ASUS CAPSULE" : "CAPSULE") : "RAW IMAGE", cap.IsCapsule ? "Accent" : "Warn");
        UpdateCapsuleSizeCheck();
        CapMergeInfo.Text = _buffer.Length == cap.BodyLength
            ? "The buffer is the same size as the capsule image. Click 'Compare with buffer' to plan which regions to keep."
            : "Read the original chip (or open its dump) so the buffer holds it, then click 'Compare with buffer'.";
    }

    void UpdateCapsuleSizeCheck()
    {
        if (_cap == null || CapSizeCheck == null) return;
        if (_chip == null) { CapSizeCheck.Text = $"Image size {ChipInfo.FormatSize(_cap.BodyLength)}."; return; }
        bool ok = _cap.BodyLength == _chip.Size;
        CapSizeCheck.Text = ok
            ? $"✓  Image size {ChipInfo.FormatSize(_cap.BodyLength)} matches the selected chip ({_chip.Name}, {_chip.SizeText})."
            : $"⚠  Image size {ChipInfo.FormatSize(_cap.BodyLength)} does not match the selected chip ({_chip.Name}, {_chip.SizeText}). Detect the chip first.";
        CapSizeCheck.Res(TextBlock.ForegroundProperty, ok ? "Good" : "Warn");
    }

    async void CapOpen_Click(object sender, RoutedEventArgs e)
    {
        var p = await PickOpenAsync("Open ASUS / AMI capsule", Ft("Capsule files", "*.cap", "*.CAP"), FtFirmware, FtAll);
        if (p != null) await CapOpenFileAsync(p);
    }

    async Task CapOpenFileAsync(string path)
    {
        try
        {
            var raw = await File.ReadAllBytesAsync(path);
            var cap = await Task.Run(() => CapsuleTool.Parse(raw, Path.GetFileName(path)));
            ShowCapsule(cap, raw);
            ShowPage(3);
            Log("CAPSULE", $"{cap.FileName}: {cap.Kind}, header 0x{cap.HeaderSize:X}, image {ChipInfo.FormatSize(cap.BodyLength)} @0x{cap.BodyOffset:X}" +
                           (cap.BoardModel != "" ? $", board {cap.BoardModel}" : "") + (cap.BiosVersion != "" ? $", BIOS {cap.BiosVersion}" : ""), cap.IsCapsule ? LogLevel.Ok : LogLevel.Warn);
            foreach (var w in cap.Warnings) Log("CAPSULE", w, LogLevel.Warn);
        }
        catch (Exception ex) { Log("CAPSULE", "Could not open capsule: " + ex.Message, LogLevel.Error); }
    }

    string CapBinName(string suffix = "")
    {
        var b = Path.GetFileNameWithoutExtension(_cap.FileName);
        return (b == "" ? "capsule" : b) + suffix + ".bin";
    }

    bool CapReady()
    {
        if (_cap != null && _cap.BodyLength > 0) return true;
        Log("CAPSULE", "Open a .CAP file first.", LogLevel.Warn);
        return false;
    }

    void CapLoadIntoBuffer()
    {
        if (!CapReady()) return;
        SetBuffer((byte[])_cap.Body.Clone(), CapBinName(), null, "CAP extract", true);
        Log("CAPSULE", $"Extracted flash image loaded into the buffer ({ChipInfo.FormatSize(_cap.BodyLength)}, CRC32 0x{Hashes.Crc32(_cap.Body):X8}).", LogLevel.Ok);
        SetStatus("Capsule image loaded — ready to program");
    }

    async void CapLoad_Click(object sender, RoutedEventArgs e)
    {
        if (!CapReady() || _busy) return;
        if (_buffer.Length > 0 && _bufSource == "Chip read" &&
            !await AskAsync("Replace buffer", "The buffer holds a chip read. Replace it with the capsule image?\n\nTip: use 'Merge with original chip dump' to keep your MAC address instead.", DialogKind.Warning, "Replace", "Cancel"))
            return;
        CapLoadIntoBuffer();
        ShowPage(0);
    }

    async void CapSave_Click(object sender, RoutedEventArgs e)
    {
        if (!CapReady()) return;
        var p = await PickSaveAsync("Save extracted flash image", CapBinName(), FtBin);
        if (p == null) return;
        await File.WriteAllBytesAsync(p, _cap.Body);
        Log("CAPSULE", $"Saved {_cap.BodyLength:N0}-byte flash image to {p} (CRC32 0x{Hashes.Crc32(_cap.Body):X8})", LogLevel.Ok);
    }

    void CapPlan_Click(object sender, RoutedEventArgs e)
    {
        if (!CapReady()) return;
        if (_buffer.Length == 0)
        {
            CapMergeInfo.Text = "The buffer is empty — read the chip (F6) or open the original dump first.";
            Log("CAPSULE", CapMergeInfo.Text, LogLevel.Warn);
            return;
        }
        if (_buffer.Length != _cap.BodyLength)
        {
            CapMergeInfo.Text = $"Size mismatch: buffer {ChipInfo.FormatSize(_buffer.Length)} vs capsule image {ChipInfo.FormatSize(_cap.BodyLength)}. A merge needs both images to be the same size.";
            Log("CAPSULE", CapMergeInfo.Text, LogLevel.Warn);
            return;
        }
        _capPlan = CapsuleTool.PlanMerge(_cap, _buffer, _analysis);
        CapMergeGrid.ItemsSource = _capPlan;
        long diff = 0;
        var body = _cap.Body;
        for (int i = 0; i < body.Length; i++) if (body[i] != _buffer[i]) diff++;
        if (_capPlan.Count == 0)
            CapMergeInfo.Text = $"No Intel flash descriptor in either image, so there are no regions to merge ({diff:N0} bytes differ). Load the capsule image directly or use Replace Region for a manual merge.";
        else
            CapMergeInfo.Text = $"{diff:N0} bytes differ between the capsule and the buffer. Tick 'Keep dump' for the regions to take from your original dump; every other region comes from the capsule.";
        Log("CAPSULE", $"Merge plan: {_capPlan.Count} regions, {diff:N0} differing bytes.", LogLevel.Accent);
    }

    async void CapMerge_Click(object sender, RoutedEventArgs e)
    {
        if (!CapReady() || _busy) return;
        if (_capPlan == null || _capPlan.Count == 0 || _buffer.Length != _cap.BodyLength)
        {
            Log("CAPSULE", "Click 'Compare with buffer' first (the buffer must hold the original dump).", LogLevel.Warn);
            return;
        }
        var keep = _capPlan.Where(r => r.KeepFromDump).Select(r => r.Name).ToList();
        if (!await AskAsync("Build merged image",
                $"New image = capsule firmware{(keep.Count > 0 ? " + from your dump: " + string.Join(", ", keep) : " (nothing kept from the dump)")}.\n\n" +
                "The merged image replaces the buffer. The original dump stays in the backup folder if auto-backup was on. Continue?", DialogKind.Question, "Merge", "Cancel"))
            return;
        if (_bufSource == "Chip read" && _set.AutoBackup == false)
            Log("CAPSULE", "Auto-backup is off — save the original dump before you program the merged image!", LogLevel.Warn);
        var merged = CapsuleTool.Merge(_cap.Body, _buffer, _capPlan, out int n);
        SetBuffer(merged, CapBinName("_merged"), null, "CAP merge", true);
        Log("CAPSULE", $"Merged image built: capsule firmware + {n} region(s) from the dump ({string.Join(", ", keep)}). CRC32 0x{Hashes.Crc32(merged):X8}", LogLevel.Ok);
        SetStatus("Merged image in buffer — review, then Auto Program (F8)");
        _capPlan = null;
        CapMergeGrid.ItemsSource = null;
        CapMergeInfo.Text = "Merged image is now in the buffer. Check the Overview (MAC, ME version), then program with Auto Program (F8).";
        ShowPage(0);
    }
}
