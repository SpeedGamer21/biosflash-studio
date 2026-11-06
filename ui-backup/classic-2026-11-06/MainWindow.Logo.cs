using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using BiosFlashStudio.Analysis;
using BiosFlashStudio.Core;
using BiosFlashStudio.Ui;
using SkiaSharp;

namespace BiosFlashStudio;

/// <summary>Row of the logo list.</summary>
public sealed class LogoItem
{
    public LogoEntry Entry { get; init; }
    public Bitmap Thumb { get; init; }
    public string Title => Entry.Title;
    public string Where => Entry.Where;
    public string Status => Entry.Replaceable ? "✓ replaceable" : "✗ " + Entry.Reason;
    public bool Replaceable => Entry.Replaceable;
    public bool NotReplaceable => !Entry.Replaceable;
}

public sealed class CheckRow
{
    public string Text { get; init; } = "";
    public bool Ok { get; init; }
    public bool Bad => !Ok;
}

/// <summary>Boot Logo page.</summary>
public partial class MainWindow
{
    List<LogoEntry> _logos = new();
    byte[] _logoScanned;
    ProtectionInfo _logoProt;
    byte[] _logoUserPic;
    LogoConversion _logoConv;
    int _logoConvGen;

    LogoEntry LogoSel => (LogoList.SelectedItem as LogoItem)?.Entry;

    static Bitmap ToBitmap(byte[] picture)
    {
        try
        {
            var png = LogoImaging.PreviewPng(picture);
            return png == null ? null : new Bitmap(new MemoryStream(png));
        }
        catch { return null; }
    }

    void LogoScan_Click(object sender, RoutedEventArgs e) => _ = LogoScanAsync(true);

    async Task LogoScanAsync(bool force)
    {
        if (_buffer.Length == 0)
        {
            LogoList.ItemsSource = null;
            LogoEmpty.Text = "Read a chip or open a firmware image, then open this page to find its boot logo.";
            LogoEmpty.IsVisible = true;
            LogoProtBanner.IsVisible = false;
            LogoShowSelected();
            return;
        }
        if (!force && ReferenceEquals(_logoScanned, _buffer)) return;
        var data = _buffer;
        _logoScanned = data;
        LogoEmpty.Text = "Scanning firmware volumes (decompressing LZMA sections)…";
        LogoEmpty.IsVisible = true;
        List<LogoEntry> logos;
        ProtectionInfo prot;
        try
        {
            (logos, prot) = await Task.Run(() => (BootLogo.Scan(data).logos, BootLogo.DetectProtection(data)));
        }
        catch (Exception ex) { LogoEmpty.Text = "Scan failed: " + ex.Message; Log("LOGO", "Scan failed: " + ex.Message, LogLevel.Error); return; }
        if (!ReferenceEquals(data, _buffer)) return;
        _logos = logos;
        _logoProt = prot;
        var items = await Task.Run(() => logos.Select(l => new LogoItem { Entry = l, Thumb = ToBitmap(l.Bytes) }).ToList());
        LogoList.ItemsSource = items;
        LogoEmpty.IsVisible = items.Count == 0;
        LogoEmpty.Text = "No BMP, JPEG, PNG or GIF picture was found. The logo may be stored with an unsupported compression (Tiano, Brotli) or a vendor format.";
        LogoProtBanner.IsVisible = prot.BootGuard;
        LogoProtText.Text = prot.Text;
        LogoOverride.IsVisible = prot.BootGuard;
        LogoOverride.IsChecked = false;
        if (items.Count > 0) LogoList.SelectedIndex = 0; else LogoShowSelected();
        Log("LOGO", $"{items.Count} picture(s) found, {items.Count(i => i.Replaceable)} replaceable{(prot.BootGuard ? " — Intel Boot Guard detected" : "")}.", items.Count > 0 ? LogLevel.Ok : LogLevel.Warn);
    }

    void LogoList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LogoShowSelected();

    void LogoShowSelected()
    {
        var l = LogoSel;
        LogoOrig.Source = (LogoList.SelectedItem as LogoItem)?.Thumb;
        if (l == null)
        {
            LogoInfo.Text = "—";
            LogoClearConversion();
            return;
        }
        var c = l.Container;
        LogoInfo.Text =
            $"{l.Info.Summary}\n" +
            $"size {l.Info.Length:N0} bytes · space for a replacement {l.Slot:N0} bytes\n" +
            $"{(l.Compressed ? "inside an LZMA-compressed volume" : "stored uncompressed")} · {l.Location}\n" +
            (c != null ? $"path: {Uefi.Describe(c)}\n" : "") +
            (l.Replaceable ? "✓ can be replaced" : "✗ " + l.Reason);
        LogoPickBtn.IsEnabled = l.Replaceable;
        _ = LogoReconvertAsync();
    }

    void LogoClearConversion()
    {
        _logoConv = null;
        LogoNew.Source = null;
        LogoNewHint.IsVisible = true;
        LogoMeter.Value = 0;
        LogoMeterText.Text = "—";
        LogoChecks.ItemsSource = null;
        LogoResult.Text = "";
        LogoApplyBtn.IsEnabled = false;
    }

    async void LogoPick_Click(object sender, RoutedEventArgs e)
    {
        if (LogoSel == null) return;
        var p = await PickOpenAsync("Choose the new boot logo", Ft("Pictures", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.PNG", "*.JPG", "*.BMP"), FtAll);
        if (p == null) return;
        _logoUserPic = await File.ReadAllBytesAsync(p);
        LogoNewName.Text = Path.GetFileName(p);
        await LogoReconvertAsync();
    }

    void LogoOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) _ = LogoReconvertAsync();
    }

    void LogoOverride_Changed(object sender, RoutedEventArgs e) => LogoUpdateApply();

    async Task LogoReconvertAsync()
    {
        var l = LogoSel;
        if (l == null || _logoUserPic == null || !l.Replaceable) { LogoClearConversion(); return; }
        int gen = ++_logoConvGen;
        var fit = LogoFitBox.SelectedIndex switch { 1 => LogoFit.Fill, 2 => LogoFit.Stretch, _ => LogoFit.Fit };
        SKColor? bg = LogoBgBox.SelectedIndex switch { 1 => SKColors.Black, 2 => SKColors.White, _ => null };
        var pic = _logoUserPic;
        LogoResult.Text = "Converting…";
        var conv = await Task.Run(() => LogoImaging.Convert(pic, l.Info, l.Bytes, l.Slot, fit, bg));
        if (gen != _logoConvGen) return;
        _logoConv = conv;
        LogoNew.Source = conv.Bytes != null ? ToBitmap(conv.Bytes) : null;
        LogoNewHint.IsVisible = LogoNew.Source == null;
        long used = conv.Bytes?.Length ?? 0;
        LogoMeter.Value = Math.Min(1000, used * 1000.0 / Math.Max(1, l.Slot));
        LogoMeterText.Text = $"{used:N0} / {l.Slot:N0} bytes";
        var rows = conv.Checks.Select(c => new CheckRow { Text = (c.ok ? "✓ " : "✗ ") + c.text, Ok = c.ok }).ToList();
        rows.AddRange(conv.Notes.Select(n => new CheckRow { Text = "• " + n, Ok = true }));
        if (conv.Bytes == null && !conv.Ok) rows.Add(new CheckRow { Text = "✗ " + conv.Error, Ok = false });
        LogoChecks.ItemsSource = rows;
        LogoResult.Text = conv.Ok ? "Ready — the firmware is rebuilt and verified when you apply." : "Fix the problems above (try 'Fit', a simpler picture, or fewer details).";
        LogoUpdateApply();
    }

    void LogoUpdateApply()
    {
        var l = LogoSel;
        bool blockedByProtection = _logoProt?.BootGuard == true && LogoOverride.IsChecked != true;
        LogoApplyBtn.IsEnabled = !_busy && l != null && l.Replaceable && _logoConv?.Ok == true && !blockedByProtection;
        if (blockedByProtection && _logoConv?.Ok == true) LogoResult.Text = "Blocked: this firmware uses Intel Boot Guard (see the warning on the left).";
    }

    async void LogoApply_Click(object sender, RoutedEventArgs e)
    {
        var l = LogoSel;
        var conv = _logoConv;
        if (_busy || l == null || conv?.Ok != true) return;
        if (!await AskAsync("Replace boot logo",
                $"Replace the {l.Info.Summary} logo ({l.Location}) in the BUFFER with your picture?\n\n" +
                (l.Compressed ? "The LZMA-compressed volume that contains it will be rebuilt. " : "") +
                "The firmware is verified after the change; nothing is written to the chip until you program it.\n\n" +
                "Test the result on this board and keep your original dump.", DialogKind.Question, "Replace", "Cancel"))
            return;
        LogoApplyBtn.IsEnabled = false;
        LogoResult.Text = l.Compressed ? "Rebuilding the compressed volume…" : "Rebuilding…";
        SetStatus("Rebuilding firmware with the new logo…", LogLevel.Accent);
        var src = _buffer;
        var res = await Task.Run(() => BootLogo.Replace(src, l, conv.Bytes));
        foreach (var c in res.Checks) Log("LOGO", (c.ok ? "✓ " : "✗ ") + c.text, c.ok ? LogLevel.Info : LogLevel.Error);
        if (!res.Ok)
        {
            LogoResult.Text = "Not applied: " + res.Error;
            Log("LOGO", "Logo replacement refused: " + res.Error, LogLevel.Error);
            SetStatus("Logo not replaced", LogLevel.Error);
            await InfoAsync("Logo not replaced", res.Error + "\n\nThe buffer was not changed.", DialogKind.Error);
            LogoUpdateApply();
            return;
        }
        Snapshot("replace boot logo");
        var name = $"{Path.GetFileNameWithoutExtension(_bufName == "" ? "image" : _bufName)}_logo.bin";
        SetBuffer(res.Image, name, null, "Logo changed", true);
        Log("LOGO", $"Boot logo replaced ({l.Info.Summary}, {conv.Bytes.Length:N0} bytes). All {res.Checks.Count} verification checks passed. CRC32 0x{Hashes.Crc32(res.Image):X8}", LogLevel.Ok);
        SetStatus("Boot logo replaced — review, save, then Auto Program (F8)");
        _logoUserPic = null;
        LogoNewName.Text = "PNG, JPEG, BMP or GIF — any size";
        await LogoScanAsync(true);
        LogoResult.Text = $"Applied and verified ({res.Checks.Count} checks). Save the image, then program it with Auto Program (F8).";
    }

    async void LogoExport_Click(object sender, RoutedEventArgs e)
    {
        var l = LogoSel;
        if (l == null) return;
        var ext = l.Info.Format switch { "JPEG" => ".jpg", "PNG" => ".png", "GIF" => ".gif", _ => ".bmp" };
        var p = await PickSaveAsync("Export logo", $"logo_{l.Info.Width}x{l.Info.Height}{ext}", Ft(l.Info.Format, "*" + ext), FtAll);
        if (p == null) return;
        await File.WriteAllBytesAsync(p, l.Bytes);
        Log("LOGO", $"Exported {l.Info.Summary} to {p}", LogLevel.Ok);
    }
}
