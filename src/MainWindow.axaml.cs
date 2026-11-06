using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BiosFlashStudio.Analysis;
using BiosFlashStudio.Controls;
using BiosFlashStudio.Core;
using BiosFlashStudio.Flash;
using BiosFlashStudio.Hardware;
using BiosFlashStudio.Platform;
using BiosFlashStudio.Ui;

namespace BiosFlashStudio;

public partial class MainWindow : Window
{
    readonly AppSettings _set;
    readonly ObservableCollection<LogEntry> _log = new();
    readonly ObservableCollection<HistoryEntry> _history;
    readonly ObservableCollection<ChipInfo> _chipItems = new();
    readonly DispatcherTimer _uiTimer, _hashTimer;
    readonly OpProgress _progress = new();
    readonly Stopwatch _opWatch = new();
    StreamWriter _logFile;
    Control[] _pages;
    bool _closeConfirmed;

    byte[] _buffer = Array.Empty<byte>();
    string _bufName = "", _bufPath, _bufSource = "";
    long _modCount;
    ChipInfo _chip;
    bool _chipDetected, _suppressChipEvent, _loadingUi, _loaded;

    bool IsI2c => ModeCombo.SelectedIndex == 1;

    static readonly (string title, string sub)[] PageInfo =
    {
        ("Overview", "Firmware identity, DMI and OEM key at a glance"),
        ("Hex Editor", "Edit the buffer byte by byte — Tab switches hex/ASCII, Ctrl+C copies the selection"),
        ("Firmware Analysis", "Intel descriptor, ME/CSE, NVRAM and flash usage"),
        ("ASUS CAP Tool", "Turn ASUS / AMI capsules into programmable images and merge them with your dump"),
        ("Repair Tools", "Clear NVRAM, clean ME, fix the MAC, repair regions from stock firmware and keep the board identity"),
        ("Update Extractor", "Pull flashable images out of vendor BIOS updates (InsydeFlash, capsules, ZIP, UEFI volumes)"),
        ("Boot Logo", "Find, export and safely replace the boot logo — uncompressed or inside LZMA-compressed volumes"),
        ("Chip Functions", "Status registers, identification and partial operations"),
        ("Chip Database", "Supported SPI flash and I²C EEPROM profiles"),
        ("Pinout & Wiring", "SOIC-8 pinouts and CH341A safety notes"),
        ("History", "Every operation with result, duration and checksum"),
        ("Settings", "Programming, verification and backup behaviour"),
        ("Diagnostics", "USB, driver and permission checks"),
    };

    public MainWindow()
    {
        InitializeComponent();
        _set = AppSettings.Load();
        Palette.Apply(_set.DarkTheme);
        UpdateThemeButton();
        var ico = AppIcon.Create();
        if (ico != null) Icon = ico;

        _pages = new Control[] { PgOverview, PgHex, PgAnalysis, PgCapsule, PgRepair, PgExtract, PgLogo, PgChipFn, PgDatabase, PgPinout, PgHistory, PgSettings, PgDiagnostics };
        _history = HistoryStore.Load();
        HistGrid.ItemsSource = _history;
        LogList.ItemsSource = _log;
        ChipCombo.ItemsSource = _chipItems;
        foreach (var g in new[] { RegionGrid, FptGrid, NvramGrid, DbGrid, HistGrid, CapMergeGrid, RepMergeGrid, RepIdGrid, ExtGrid })
            foreach (var col in g.Columns.OfType<DataGridTextColumn>().Where(c => c.Tag as string == "mono"))
            {
                col.FontFamily = Palette.Mono;
                col.FontSize = 12;
            }

        Hex.ScrollBar = HexScroll;
        Hex.SelectionChanged += (_, _) => UpdateHexStatus();
        Hex.DataEdited += (_, _) => { _hashTimer.Stop(); _hashTimer.Start(); };

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _uiTimer.Tick += (_, _) => UpdateProgressUi();
        _hashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _hashTimer.Tick += (_, _) =>
        {
            _hashTimer.Stop();
            _modCount = Hex.ModifiedCount();
            UpdateBufferInfo();
            UpdateHexStatus();
        };

        try
        {
            var dir = Path.Combine(AppSettings.Dir, "logs");
            Directory.CreateDirectory(dir);
            _logFile = new StreamWriter(Path.Combine(dir, $"session_{DateTime.Now:yyyyMMdd}.log"), true) { AutoFlush = true };
        }
        catch { _logFile = null; }

        _loadingUi = true;
        LoadSettingsToUi();
        foreach (var (kind, text) in ProgrammerFactory.Available())
            BackendCombo.Items.Add(new ComboBoxItem { Content = text, Tag = kind });
        var want = Enum.TryParse<BackendKind>(_set.Backend, out var bk) ? bk : BackendKind.Auto;
        BackendCombo.SelectedItem = BackendCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (BackendKind)i.Tag == want) ?? BackendCombo.Items[0];
        _loadingUi = false;

        PlatformText.Text = PlatformUtil.OsName;
        UdevBtn.IsVisible = OperatingSystem.IsLinux();
        SetLogVisible(_set.ShowLog);

        FillChipList();
        var last = ChipDatabase.SpiChips.FirstOrDefault(c => c.Display == _set.LastChip);
        SelectChip(last ?? ChipDatabase.SpiChips.First(c => c.JedecId == 0xEF4018), false);

        DbGrid.ItemsSource = ChipDatabase.All;
        DbCount.Text = $"{ChipDatabase.SpiChips.Count()} SPI flash  ·  {ChipDatabase.I2cChips.Count()} I²C EEPROM profiles";
        BuildPinout();
        UpdateHardwareCard();
        UpdateBufferInfo();
        UpdateHexStatus();
        ShowAnalysis(null);
        ShowCapsule(null);
        DiagOut.Text = AboutText();
        ShowPage(0);

        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        Closing += Window_Closing;

        Log("SYSTEM", $"BiosFlash Studio 2.0 started on {PlatformUtil.OsName}", LogLevel.Accent);
        Opened += async (_, _) =>
        {
            _loaded = true;
            QuickUsbProbe();
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
            {
                BackendCombo.SelectedItem = BackendCombo.Items.OfType<ComboBoxItem>().First(i => (BackendKind)i.Tag == BackendKind.Simulator);
                await DetectAsync();
                await ReadAsync();
            }
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            var fileArg = args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
            if (fileArg != null)
            {
                if (fileArg.EndsWith(".cap", StringComparison.OrdinalIgnoreCase)) await CapOpenFileAsync(fileArg);
                else await LoadFileAsync(fileArg);
            }
            if (args.Contains("--light")) { Palette.Apply(false); ThemeIco.Kind = "moon"; ThemeText.Text = "Dark theme"; }
            var pageArg = args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
            if (pageArg != null && int.TryParse(pageArg[7..], out var pg)) ShowPage(pg);
            if (args.Contains("--compare")) CapPlan_Click(null, null);
            if (args.Contains("--diag")) { ShowPage(12); DriverCheck_Click(null, null); }
            var picArg = args.FirstOrDefault(a => a.StartsWith("--logo-pic=", StringComparison.OrdinalIgnoreCase));
            if (picArg != null && File.Exists(picArg[11..])) { ShowPage(6); await LogoScanAsync(true); _logoUserPic = await File.ReadAllBytesAsync(picArg[11..]); LogoNewName.Text = Path.GetFileName(picArg[11..]); await LogoReconvertAsync(); PgLogo.ScrollToEnd(); }
            var stockArg = args.FirstOrDefault(a => a.StartsWith("--stock=", StringComparison.OrdinalIgnoreCase));
            if (stockArg != null && File.Exists(stockArg[8..])) await RepLoadStockFileAsync(stockArg[8..]);
            var extArg = args.FirstOrDefault(a => a.StartsWith("--extract=", StringComparison.OrdinalIgnoreCase));
            if (extArg != null && File.Exists(extArg[10..])) await ExtOpenFileAsync(extArg[10..]);
        };
    }

    async void Window_Closing(object sender, WindowClosingEventArgs e)
    {
        if (_busy && !_closeConfirmed)
        {
            e.Cancel = true;
            if (await AskAsync("Operation running", "An operation is running. Cancel it and exit?", DialogKind.Warning, "Exit", "Stay"))
            {
                _closeConfirmed = true;
                _cts?.Cancel();
                Close();
            }
            return;
        }
        _cts?.Cancel();
        ReadSettingsFromUi();
        _set.Save();
        HistoryStore.Save(_history.Reverse());
        try { _prog?.Close(); } catch { }
        _logFile?.Dispose();
    }

    // =====================================================================  navigation
    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem { Tag: string t } && int.TryParse(t, out var i)) ShowPage(i, false);
    }

    void ShowPage(int index, bool syncNav = true)
    {
        if (_pages == null || index < 0 || index >= _pages.Length) return;
        for (int i = 0; i < _pages.Length; i++) _pages[i].IsVisible = i == index;
        PageTitle.Text = PageInfo[index].title;
        PageSub.Text = PageInfo[index].sub;
        if (syncNav)
        {
            var item = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(x => x.Tag as string == index.ToString());
            if (item != null && Nav.SelectedItem != item) Nav.SelectedItem = item;
        }
        if (index == 1) Dispatcher.UIThread.Post(() => Hex.Focus(), DispatcherPriority.Background);
        if (index == 6) _ = LogoScanAsync(false);
    }

    void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        _set.ShowLog = !LogPanel.IsVisible;
        SetLogVisible(_set.ShowLog);
    }

    void SetLogVisible(bool show)
    {
        LogPanel.IsVisible = show;
        LogSplitter.IsVisible = show;
        WorkGrid.ColumnDefinitions[1].Width = new GridLength(show ? 12 : 0);
        WorkGrid.ColumnDefinitions[2].Width = show ? new GridLength(380) : new GridLength(0);
    }

    // =====================================================================  dialogs / pickers
    Task<bool> AskAsync(string title, string text, DialogKind kind = DialogKind.Question, string yes = "Yes", string no = "No")
        => Dialogs.Ask(this, title, text, yes, no, kind);

    Task InfoAsync(string title, string text, DialogKind kind = DialogKind.Info) => Dialogs.Info(this, title, text, kind);

    static FilePickerFileType Ft(string name, params string[] patterns) => new(name) { Patterns = patterns };

    static readonly FilePickerFileType FtAll = Ft("All files", "*");
    static readonly FilePickerFileType FtFirmware = Ft("Firmware images", "*.bin", "*.rom", "*.fd", "*.cap", "*.CAP", "*.img", "*.bio", "*.upd", "*.BIN", "*.ROM");
    static readonly FilePickerFileType FtBin = Ft("Binary image", "*.bin");

    async Task<IStorageFolder> StartFolder(string path)
    {
        try { return Directory.Exists(path) ? await StorageProvider.TryGetFolderFromPathAsync(path) : null; }
        catch { return null; }
    }

    async Task<string> PickOpenAsync(string title, params FilePickerFileType[] types)
    {
        var res = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types.Length > 0 ? types : new[] { FtAll },
            SuggestedStartLocation = await StartFolder(_set.LastFolder),
        });
        var p = res.FirstOrDefault()?.TryGetLocalPath();
        if (p != null) _set.LastFolder = Path.GetDirectoryName(p);
        return p;
    }

    async Task<string> PickSaveAsync(string title, string suggested, params FilePickerFileType[] types)
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested,
            FileTypeChoices = types.Length > 0 ? types : new[] { FtAll },
            DefaultExtension = Path.GetExtension(suggested)?.TrimStart('.'),
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await StartFolder(_set.LastFolder),
        });
        var p = f?.TryGetLocalPath();
        if (p != null) _set.LastFolder = Path.GetDirectoryName(p);
        return p;
    }

    async Task<string> PickFolderAsync(string title, string start)
    {
        var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, SuggestedStartLocation = await StartFolder(start) });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    // =====================================================================  logging / status
    public void Log(string cat, string msg, LogLevel level = LogLevel.Info)
    {
        var e = new LogEntry { Category = cat, Message = msg, Level = level };
        try { _logFile?.WriteLine(e.ToString()); } catch { }
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => AddLog(e)); return; }
        AddLog(e);
    }

    void AddLog(LogEntry e)
    {
        _log.Add(e);
        if (_log.Count > 5000) _log.RemoveAt(0);
        if (LogPanel.IsVisible) LogList.ScrollIntoView(e);
        LogCount.Text = $"{_log.Count} events";
    }

    void SetStatus(string text, LogLevel level = LogLevel.Ok)
    {
        StatusText.Text = text;
        StatusDot.Res(Avalonia.Controls.Shapes.Shape.FillProperty, level switch { LogLevel.Error => "Bad", LogLevel.Warn => "Warn", LogLevel.Accent => "Info", _ => "Good" });
    }

    void ClearLog_Click(object sender, RoutedEventArgs e) { _log.Clear(); LogCount.Text = "0 events"; }

    async void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var p = await PickSaveAsync("Save log", $"biosflash_{DateTime.Now:yyyyMMdd_HHmmss}.log", Ft("Log file", "*.log", "*.txt"));
        if (p == null) return;
        await File.WriteAllLinesAsync(p, _log.Select(x => x.ToString()));
        Log("SYSTEM", "Log saved to " + p, LogLevel.Ok);
    }

    // =====================================================================  theme / keys
    void UpdateThemeButton()
    {
        ThemeIco.Kind = _set.DarkTheme ? "sun" : "moon";
        ThemeText.Text = _set.DarkTheme ? "Light theme" : "Dark theme";
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        _set.DarkTheme = !_set.DarkTheme;
        Palette.Apply(_set.DarkTheme);
        UpdateThemeButton();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateHardwareCard();
            UpdateChipCard();
            UpdateBufferInfo();
            Hex.RefreshView();
        }, DispatcherPriority.Background);
        _set.Save();
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.F5: Detect_Click(null, null); break;
            case Key.F6: Read_Click(null, null); break;
            case Key.F4: Blank_Click(null, null); break;
            case Key.F7: Verify_Click(null, null); break;
            case Key.F8: Auto_Click(null, null); break;
            case Key.F9: Program_Click(null, null); break;
            case Key.Escape when _busy: Cancel_Click(null, null); break;
            case Key.O when ctrl: Open_Click(null, null); break;
            case Key.S when ctrl: Save_Click(null, null); break;
            default: return;
        }
        e.Handled = true;
    }

    // =====================================================================  chip selection
    void FillChipList()
    {
        _suppressChipEvent = true;
        _chipItems.Clear();
        var src = IsI2c ? ChipDatabase.I2cChips : ChipDatabase.SpiChips.OrderBy(c => c.Manufacturer).ThenBy(c => c.Size).ThenBy(c => c.Name);
        foreach (var c in src) _chipItems.Add(c);
        _suppressChipEvent = false;
    }

    void SelectChip(ChipInfo chip, bool detected)
    {
        if (chip == null) return;
        if (!_chipItems.Contains(chip)) _chipItems.Insert(0, chip);
        _suppressChipEvent = true;
        ChipCombo.SelectedItem = chip;
        _suppressChipEvent = false;
        _chip = chip;
        _chipDetected = detected;
        _set.LastChip = chip.Display;
        UpdateChipCard();
        UpdateBufferInfo();
        UpdateCapsuleSizeCheck();
    }

    void ChipCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChipEvent || ChipCombo.SelectedItem is not ChipInfo c) return;
        _chip = c;
        _chipDetected = false;
        _set.LastChip = c.Display;
        UpdateChipCard();
        UpdateBufferInfo();
        UpdateCapsuleSizeCheck();
        if (_loaded) Log("CHIP", $"Profile selected manually: {c.Display}");
    }

    void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        FillChipList();
        SelectChip(IsI2c ? ChipDatabase.I2cChips.First(c => c.Name == "24C02") : ChipDatabase.SpiChips.First(c => c.JedecId == 0xEF4018), false);
        UpdateHardwareCard();
        Log("MODE", IsI2c ? "Switched to I²C EEPROM (24xx) mode — pick the exact chip type, EEPROMs cannot be auto-detected." : "Switched to SPI flash (25xx) mode.", LogLevel.Accent);
    }

    void SetPill(Border pill, TextBlock text, string label, string key)
    {

        pill.Res(Border.BorderBrushProperty, key);
        text.Res(TextBlock.ForegroundProperty, key);
        text.Text = label;
    }

    void UpdateChipCard()
    {
        var c = _chip;
        if (c == null)
        {
            ChipMfr.Text = ChipPart.Text = ChipJedec.Text = ChipCap.Text = ChipVolt.Text = ChipPage.Text = "—";
            return;
        }
        ChipMfr.Text = c.Manufacturer;
        ChipPart.Text = c.Name;
        ChipJedec.Text = c.Kind == ChipKind.I2cEeprom ? "n/a (I²C EEPROM)" : $"{c.JedecId >> 16:X2} {(c.JedecId >> 8) & 0xFF:X2} {c.JedecId & 0xFF:X2}";
        ChipCap.Text = c.Size >= 1024 * 1024 ? $"{c.Size / 1048576.0:0.##} MB ({c.Size * 8 / 1048576} Mbit)" : $"{c.SizeText} ({c.Size * 8 / 1024} Kbit)";
        ChipVolt.Text = c.Is18V ? $"{c.Voltage} · 1.8 V adapter required!" : c.Kind == ChipKind.I2cEeprom ? c.Voltage : $"{c.Voltage} · jumper at 3.3 V";
        ChipVolt.Res(TextBlock.ForegroundProperty, c.Is18V ? "Bad" : "Txt");
        ChipPage.Text = c.Kind == ChipKind.I2cEeprom ? $"{c.PageSize} B · I²C" : $"{c.PageSize} B · {(c.NeedsFourByte ? "4-byte" : "3-byte")} addressing";
        SetPill(ChipPill, ChipPillText, _chipDetected ? "DETECTED" : "MANUAL", _chipDetected ? "Good" : "TxtDim");
    }

    // =====================================================================  buffer
    void SetBuffer(byte[] data, string name, string path, string source, bool analyze)
    {
        _buffer = data ?? Array.Empty<byte>();
        _bufName = name ?? "";
        _bufPath = path;
        _bufSource = source ?? "";
        _modCount = 0;
        Hex.SetData(_buffer);
        HexSource.Text = _buffer.Length == 0 ? "EMPTY" : _bufSource.ToUpperInvariant();
        UpdateBufferInfo();
        UpdateHexStatus();
        if (_buffer.Length == 0) ShowAnalysis(null);
        else if (analyze && _set.AutoAnalyze) _ = AnalyzeBufferAsync();
        if (PgLogo.IsVisible) _ = LogoScanAsync(false);
    }

    void UpdateBufferInfo()
    {
        if (_buffer.Length == 0)
        {
            BufSize.Text = BufCrc.Text = BufSha.Text = BufSum.Text = "—";
            BufName.Text = "No image loaded";
            BufStatus.Text = "Read a chip, open a file or drop one here";
            BufStatus.Res(TextBlock.ForegroundProperty, "TxtDim");
            SetPill(BufPill, BufPillText, "EMPTY", "TxtDim");
            return;
        }
        BufName.Text = _bufName;
        BufSize.Text = $"{ChipInfo.FormatSize(_buffer.Length)} ({_buffer.Length:N0} bytes)";
        BufCrc.Text = $"0x{Hashes.Crc32(_buffer):X8}";
        BufSha.Text = Hashes.Sha256(_buffer);
        BufSum.Text = $"0x{Hashes.Sum32(_buffer):X8} (Sum32)";
        string status;
        string key = "Good";
        if (_modCount > 0) { status = $"Modified — {_modCount:N0} byte(s) edited"; key = "Warn"; }
        else status = _bufSource == "Chip read" ? "Read from chip" : _bufSource == "File" ? "Loaded from file" : _bufSource;
        if (_chip != null && _chip.Size != _buffer.Length)
        {
            status += $" · size ≠ chip ({_chip.SizeText})";
            key = "Warn";
        }
        BufStatus.Text = status;
        BufStatus.Res(TextBlock.ForegroundProperty, key);
        SetPill(BufPill, BufPillText, _modCount > 0 ? "MODIFIED" : "LOADED", _modCount > 0 ? "Warn" : "Good");
    }

    async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var p = await PickOpenAsync("Open firmware image", FtFirmware, FtAll);
        if (p != null) await LoadFileAsync(p);
    }

    async Task LoadFileAsync(string path)
    {
        try
        {
            var data = await File.ReadAllBytesAsync(path);
            _set.LastFolder = Path.GetDirectoryName(path);
            if (CapsuleTool.LooksLikeCapsule(data) && (_chip == null || data.Length != _chip.Size))
            {
                var cap = await Task.Run(() => CapsuleTool.Parse(data, Path.GetFileName(path)));
                if (cap.IsCapsule)
                {
                    ShowCapsule(cap, data);
                    Log("CAPSULE", $"{Path.GetFileName(path)} is a {cap.Kind} — header 0x{cap.HeaderSize:X}, image {ChipInfo.FormatSize(cap.BodyLength)}.", LogLevel.Accent);
                    if (await AskAsync("Capsule file detected",
                            $"{Path.GetFileName(path)} is a {cap.Kind} with a {cap.BodyOffset:N0}-byte header.\n\n" +
                            $"Load the extracted {ChipInfo.FormatSize(cap.BodyLength)} flash image into the buffer?\n\n" +
                            "Choose 'Open CAP Tool' to inspect it or merge it with your chip dump first.", DialogKind.Question, "Load image", "Open CAP Tool"))
                    {
                        CapLoadIntoBuffer();
                    }
                    else ShowPage(3);
                    return;
                }
            }
            SetBuffer(data, Path.GetFileName(path), path, "File", true);
            Log("FILE", $"Opened {Path.GetFileName(path)} — {data.Length:N0} bytes, CRC32 0x{Hashes.Crc32(data):X8}", LogLevel.Ok);
            if (_chip != null && data.Length != _chip.Size)
                Log("FILE", $"Image size ({ChipInfo.FormatSize(data.Length)}) differs from selected chip ({_chip.SizeText}).", LogLevel.Warn);
            SetStatus("Loaded " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log("FILE", "Open failed: " + ex.Message, LogLevel.Error);
        }
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_buffer.Length == 0) { Log("FILE", "Nothing to save — buffer is empty.", LogLevel.Warn); return; }
        var p = await PickSaveAsync("Save buffer", _bufSource == "File" && !string.IsNullOrEmpty(_bufName) ? _bufName : SuggestedName(), FtBin, Ft("ROM image", "*.rom"), FtAll);
        if (p == null) return;
        try
        {
            await File.WriteAllBytesAsync(p, _buffer);
            _bufPath = p;
            _bufName = Path.GetFileName(p);
            Hex.AcceptEdits();
            _modCount = 0;
            UpdateBufferInfo();
            Log("FILE", $"Saved {_buffer.Length:N0} bytes to {p} (CRC32 0x{Hashes.Crc32(_buffer):X8})", LogLevel.Ok);
            SetStatus("Saved " + _bufName);
        }
        catch (Exception ex) { Log("FILE", "Save failed: " + ex.Message, LogLevel.Error); }
    }

    string SuggestedName()
    {
        string model = _analysis?.Dmi1("MODEL") ?? "";
        string chip = _chip?.Name.Split('/')[0] ?? "dump";
        var baseName = string.IsNullOrEmpty(model) ? chip : $"{model}_{chip}";
        foreach (var ch in Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' })) baseName = baseName.Replace(ch, '_');
        return $"{baseName.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
    }

    async void ClearBuffer_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_modCount > 0 && !await AskAsync("Clear buffer", "Discard the edited buffer?")) return;
        SetBuffer(Array.Empty<byte>(), "", null, "", false);
        Log("BUFFER", "Buffer cleared.");
    }

#pragma warning disable CS0618 // classic drag&drop API (still supported in 11.x)
    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) && !_busy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busy) return;
        var f = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (f == null || !File.Exists(f)) return;
        if (PgCapsule.IsVisible) await CapOpenFileAsync(f);
        else if (PgExtract.IsVisible) await ExtOpenFileAsync(f);
        else if (PgRepair.IsVisible) await RepLoadStockFileAsync(f);
        else await LoadFileAsync(f);
    }

    // =====================================================================  hex editor
    void UpdateHexStatus()
    {
        if (_buffer.Length == 0) { HexStatus.Text = "Buffer: 0 bytes"; return; }
        long c = Hex.CursorOffset;
        byte v = _buffer[c];
        var sb = new StringBuilder();
        sb.Append($"Buffer {_buffer.Length:N0} B ({ChipInfo.FormatSize(_buffer.Length)})   Offset 0x{c:X8}   Value 0x{v:X2} ({v})");
        if (Hex.SelectionLength > 1) sb.Append($"   Sel 0x{Hex.SelectionStart:X8} +0x{Hex.SelectionLength:X} ({Hex.SelectionLength:N0} B)");
        if (_modCount > 0) sb.Append($"   Modified {_modCount:N0}");
        var reg = _analysis?.Regions.FirstOrDefault(r => c >= r.Start && c <= r.End);
        if (reg != null) sb.Append($"   Region {reg.Name}");
        HexStatus.Text = sb.ToString();
    }

    public static bool TryParseNum(string s, out long v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().Replace("_", "").Replace(" ", "");
        if (s.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { s = s[..^1]; return long.TryParse(s, out v) && (v *= 1024) >= 0; }
        if (s.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { s = s[..^1]; return long.TryParse(s, out v) && (v *= 1024 * 1024) >= 0; }
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (s.EndsWith("h", StringComparison.OrdinalIgnoreCase)) s = s[..^1];
        return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v);
    }

    void Goto_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseNum(GotoBox.Text, out var a) || a >= _buffer.Length) { Log("HEX", "Invalid address: " + GotoBox.Text, LogLevel.Warn); return; }
        Hex.GoTo(a);
        Hex.Focus();
    }

    void GotoBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Goto_Click(null, null); }
    void FindBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Find_Click(null, null); }

    void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_buffer.Length == 0 || string.IsNullOrEmpty(FindBox.Text)) return;
        byte[] pat;
        try
        {
            pat = FindMode.SelectedIndex switch
            {
                1 => Encoding.ASCII.GetBytes(FindBox.Text),
                2 => Encoding.Unicode.GetBytes(FindBox.Text),
                _ => Convert.FromHexString(FindBox.Text.Replace(" ", "").Replace("0x", "")),
            };
        }
        catch { Log("HEX", "Invalid hex pattern.", LogLevel.Warn); return; }
        long from = Hex.SelectionLength > 0 ? Hex.SelectionStart + 1 : 0;
        long at = Hex.Find(pat, from);
        if (at < 0 && from > 0) at = Hex.Find(pat, 0);
        if (at < 0) { Log("HEX", $"Pattern not found ({pat.Length} bytes).", LogLevel.Warn); SetStatus("Pattern not found", LogLevel.Warn); return; }
        Hex.Select(at, pat.Length);
        SetStatus($"Found at 0x{at:X8}");
    }

    async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_buffer.Length == 0) { Log("HEX", "Load or read an image first.", LogLevel.Warn); return; }
        var p = await PickOpenAsync("Compare buffer with file", FtFirmware, FtAll);
        if (p == null) return;
        var other = await File.ReadAllBytesAsync(p);
        long n = Math.Min(other.Length, _buffer.Length), diff = 0, first = -1;
        var ranges = new List<string>();
        long runStart = -1;
        for (long i = 0; i <= n; i++)
        {
            bool d = i < n && other[i] != _buffer[i];
            if (d) { diff++; if (first < 0) first = i; if (runStart < 0) runStart = i; }
            else if (runStart >= 0) { if (ranges.Count < 12) ranges.Add($"0x{runStart:X8}-0x{i - 1:X8}"); runStart = -1; }
        }
        Log("COMPARE", $"{Path.GetFileName(p)}: {diff:N0} differing bytes" + (other.Length != _buffer.Length ? $" (sizes differ: {other.Length:N0} vs {_buffer.Length:N0})" : ""), diff == 0 ? LogLevel.Ok : LogLevel.Warn);
        foreach (var r in ranges) Log("COMPARE", "  diff " + r, LogLevel.Warn);
        if (first >= 0) Hex.GoTo(first);
        await InfoAsync("Compare", diff == 0 && other.Length == _buffer.Length ? "The files are IDENTICAL." : $"{diff:N0} bytes differ.\nFirst difference at 0x{first:X8}.\n\nSee the log for the difference ranges.",
            diff == 0 ? DialogKind.Info : DialogKind.Warning);
    }

    void Revert_Click(object sender, RoutedEventArgs e)
    {
        Hex.RevertEdits();
        _modCount = 0;
        UpdateBufferInfo();
        UpdateHexStatus();
        Log("HEX", "Edits reverted.");
    }

    // =====================================================================  database
    void DbSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = DbSearch.Text?.Trim() ?? "";
        DbGrid.ItemsSource = q.Length == 0 ? ChipDatabase.All : ChipDatabase.All.Where(c =>
            c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Manufacturer.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            c.JedecText.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void DbGrid_DoubleTapped(object sender, TappedEventArgs e) => UseChip_Click(null, null);

    void UseChip_Click(object sender, RoutedEventArgs e)
    {
        if (DbGrid.SelectedItem is not ChipInfo c) return;
        bool i2c = c.Kind == ChipKind.I2cEeprom;
        if (i2c != IsI2c) ModeCombo.SelectedIndex = i2c ? 1 : 0;
        SelectChip(c, false);
        Log("CHIP", "Profile selected from database: " + c.Display, LogLevel.Accent);
    }

    // =====================================================================  history
    void AddHistory(string op, string result, TimeSpan dur, string details, byte[] data = null)
    {
        _history.Insert(0, new HistoryEntry
        {
            Time = DateTime.Now,
            Operation = op,
            Chip = _chip?.Name ?? "",
            Result = result,
            Duration = dur.TotalSeconds < 60 ? $"{dur.TotalSeconds:0.0}s" : dur.ToString(@"mm\:ss"),
            Size = _chip?.SizeText ?? "",
            Crc32 = data != null ? $"{Hashes.Crc32(data):X8}" : "",
            Details = details ?? "",
        });
        HistoryStore.Save(_history.Reverse());
    }

    async void HistExport_Click(object sender, RoutedEventArgs e)
    {
        var p = await PickSaveAsync("Export history", "operation_history.csv", Ft("CSV", "*.csv"));
        if (p == null) return;
        var sb = new StringBuilder("Time,Operation,Chip,Result,Duration,Size,CRC32,Details\r\n");
        foreach (var h in _history)
            sb.Append($"\"{h.TimeText}\",\"{h.Operation}\",\"{h.Chip}\",\"{h.Result}\",\"{h.Duration}\",\"{h.Size}\",\"{h.Crc32}\",\"{h.Details.Replace("\"", "'")}\"\r\n");
        await File.WriteAllTextAsync(p, sb.ToString());
        Log("HISTORY", "History exported to " + p, LogLevel.Ok);
    }

    async void HistClear_Click(object sender, RoutedEventArgs e)
    {
        if (!await AskAsync("History", "Clear the entire operation history?", DialogKind.Warning, "Clear", "Cancel")) return;
        _history.Clear();
        HistoryStore.Save(_history);
    }

    // =====================================================================  pinout
    void BuildPinout()
    {
        void Add(string title, params string[] pins)
        {
            var b = new Border { Child = new PinoutDiagram { Title = title, Pins = pins, Width = 330, Height = 196 }, Margin = new Thickness(0, 0, 12, 12) };
            b.Classes.Add("card");
            PinoutPanel.Children.Add(b);
        }
        Add("25xx SPI NOR flash  (SOIC-8 / DIP-8 / WSON-8)", "CS#", "DO (IO1)", "WP# (IO2)", "GND", "DI (IO0)", "CLK", "HOLD# (IO3)", "VCC");
        Add("24xx I²C EEPROM  (SOIC-8 / DIP-8)", "A0", "A1", "A2", "GND", "SDA", "SCL", "WP", "VCC");
        Add("93Cxx Microwire EEPROM  (reference only)", "CS", "SK", "DI", "DO", "GND", "ORG", "NC/DU", "VCC");
        RefText.Text =
            "• CH341A ZIF socket: 25xx SPI chips go in the rows marked \"25 SPI\" (closest to the lever), 24xx EEPROMs in the \"24\" rows. Pin 1 (dot/notch) faces the lever.\n" +
            "• The classic black CH341A board drives its data lines at 5 V even though VCC is 3.3 V. Use the 3.3 V-modded version or a level-shifting adapter on modern 3.3 V BIOS chips.\n" +
            "• 1.8 V chips (W25Q…FW/JW, MX25U…, GD25LQ…, IS25WP…) must go through a 1.8 V adapter. Running them at 3.3 V can kill the chip.\n" +
            "• In-circuit (SOIC-8 clip): unplug AC, remove the main battery and CMOS coin cell. If detection fails, the board is back-powered — lift VCC or desolder the chip.\n" +
            "• Wiggle the clip and run Detect until the JEDEC ID is stable. Enable \"Read chip twice\" in Settings: a reliable read gives the same CRC32 on both passes.\n" +
            "• ALWAYS save the original dump (auto-backup is on by default) before erasing. It contains the unique serial number, MAC, OEM key, ME config and NVRAM.\n" +
            "• ASUS .CAP files carry a capsule header — use the ASUS CAP Tool to strip it and to keep your board's MAC (GbE region) from the original dump.\n" +
            "• 32 MB chips (W25Q256, MX25L256…) use 4-byte addressing — handled automatically.\n" +
            "• Windows drivers: WCH CH341PAR (CH341DLLA64.DLL) or WinUSB via Zadig.  Linux: libusb-1.0 + the udev rule from Diagnostics (no root needed afterwards).\n" +
            "• CH341 in UART mode (PID 5523) needs the jumper moved to the I²C/SPI position (pins 1-2).";
    }

    // =====================================================================  settings
    void LoadSettingsToUi()
    {
        SetVerify.IsChecked = _set.VerifyAfterProgram;
        SetSmart.IsChecked = _set.SmartProgram;
        SetUnprotect.IsChecked = _set.UnprotectBeforeWrite;
        SetSkipBlank.IsChecked = _set.SkipBlankPages;
        Set4Byte.IsChecked = _set.Use4ByteMode;
        SetConfirm.IsChecked = _set.ConfirmDestructive;
        SetDoubleRead.IsChecked = _set.DoubleRead;
        SetAutoAnalyze.IsChecked = _set.AutoAnalyze;
        SetAutoBackup.IsChecked = _set.AutoBackup;
        SetBackupFolder.Text = _set.BackupFolder;
        SetEraseMode.SelectedIndex = _set.EraseMode switch { "Block" => 1, "Sector" => 2, _ => 0 };
        SetSpeed.SelectedIndex = Math.Clamp(_set.SpeedIndex, 0, 3);
    }

    void ReadSettingsFromUi()
    {
        _set.VerifyAfterProgram = SetVerify.IsChecked == true;
        _set.SmartProgram = SetSmart.IsChecked == true;
        _set.UnprotectBeforeWrite = SetUnprotect.IsChecked == true;
        _set.SkipBlankPages = SetSkipBlank.IsChecked == true;
        _set.Use4ByteMode = Set4Byte.IsChecked == true;
        _set.ConfirmDestructive = SetConfirm.IsChecked == true;
        _set.DoubleRead = SetDoubleRead.IsChecked == true;
        _set.AutoAnalyze = SetAutoAnalyze.IsChecked == true;
        _set.AutoBackup = SetAutoBackup.IsChecked == true;
        _set.BackupFolder = (SetBackupFolder.Text ?? "").Trim();
        _set.EraseMode = (SetEraseMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "Chip";
        _set.SpeedIndex = Math.Max(0, SetSpeed.SelectedIndex);
        _set.Backend = CurrentBackend().ToString();
    }

    void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUi();
        _set.Save();
        try { if (_prog?.IsOpen == true) _prog.SetSpeed(_set.SpeedIndex); } catch { }
        UpdateHardwareCard();
        Log("SETTINGS", "Settings saved.", LogLevel.Ok);
    }

    async void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var f = await PickFolderAsync("Backup folder", SetBackupFolder.Text);
        if (f != null) SetBackupFolder.Text = f;
    }

    void OpenBackupFolder_Click(object sender, RoutedEventArgs e) => PlatformUtil.OpenFolder(_set.BackupFolder);
    void OpenDataFolder_Click(object sender, RoutedEventArgs e) => PlatformUtil.OpenFolder(AppSettings.Dir);

    // =====================================================================  diagnostics
    void QuickUsbProbe()
    {
        var devs = UsbScanner.ScanWch().Where(d => d.Present).ToList();
        if (devs.Count == 0) { Log("USB", "No WCH CH341 device detected. Plug in the programmer or choose the Simulator backend.", LogLevel.Warn); return; }
        foreach (var d in devs)
        {
            var lvl = d.IsCh341Spi && d.Accessible ? LogLevel.Ok : LogLevel.Warn;
            string note;
            if (OperatingSystem.IsWindows())
                note = d.IsCh341Spi ? (d.IsWinUsb ? "WinUSB" : d.IsWchDriver ? "WCH driver" : d.IsSerialDriver ? $"WRONG DRIVER {d.Service} (serial) — install CH341PAR or WinUSB" : d.Service == "" ? "NO DRIVER" : d.Service)
                     : d.IsCh341Uart ? $"UART mode ({(d.Service == "" ? "no driver" : d.Service)}) — move the jumper to pins 1-2 (SPI/I²C), CH341SER cannot program chips" : d.Service;
            else
                note = d.IsCh341Uart ? "UART mode — move jumper to SPI/I²C" : d.Accessible ? "accessible" : "PERMISSION DENIED — install the udev rule (Diagnostics)";
            Log("USB", $"Found {d.Vid:X4}:{d.Pid:X4} ({note}) {d.Description}", lvl);
        }
        if (OperatingSystem.IsLinux() && devs.Any(d => d.IsCh341Spi && !d.Accessible) && !PlatformUtil.UdevRuleInstalled())
            SetStatus("CH341A found but not accessible — Diagnostics → Install udev rule", LogLevel.Warn);
    }

    void ScanUsb_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"USB scan @ {DateTime.Now:HH:mm:ss} — WCH devices (VID 1A86)");
        sb.AppendLine(new string('=', 60));
        var devs = UsbScanner.ScanWch();
        if (devs.Count == 0) sb.AppendLine(OperatingSystem.IsWindows() ? "No WCH devices have ever been seen on this PC." : "No WCH devices on the USB bus (check 'lsusb').");
        foreach (var d in devs.OrderByDescending(d => d.Present))
        {
            sb.AppendLine(d.ToString());
            if (d.IsCh341Spi && d.Present)
            {
                if (OperatingSystem.IsWindows())
                {
                    if (d.IsWinUsb) sb.AppendLine($"    -> WinUSB interface: {UsbScanner.GetInterfacePath(d) ?? "(not found)"}");
                    else if (d.IsWchDriver) sb.AppendLine("    -> WCH vendor driver: use the 'WCH driver (DLL)' backend");
                }
                else if (!d.Accessible) sb.AppendLine("    -> No permission: click 'Install udev rule', then re-plug the programmer.");
                else sb.AppendLine("    -> Ready for the libusb backend.");
            }
            if (d.IsCh341Uart && d.Present) sb.AppendLine("    -> This is the CH341 in UART/serial mode (CH341SER = serial driver). It cannot program chips: move the jumper to pins 1-2 (SPI/I²C), re-plug, then install CH341PAR or WinUSB.");
            if (d.IsCh341Spi && d.Present && d.IsSerialDriver) sb.AppendLine($"    -> Wrong driver: {d.Service} is the serial driver. Install CH341PAR (wch-ic.com) or WinUSB with Zadig for this device.");
        }
        DiagOut.Text = sb.ToString();
    }

    void DriverCheck_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OS          : {PlatformUtil.OsName}");
        sb.AppendLine($"Process     : {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} · .NET {Environment.Version}");
        if (OperatingSystem.IsWindows())
        {
            var err = Ch341DllProgrammer.TryLoadLibrary();
            sb.AppendLine(err == null ? $"WCH DLL     : OK ({Ch341DllProgrammer.LibraryPath})" : $"WCH DLL     : {err}");
            sb.AppendLine($"WinUSB      : {(File.Exists(Path.Combine(Environment.SystemDirectory, "winusb.dll")) ? "winusb.dll present" : "missing")}");
        }
        var lu = LibUsb.TryLoad();
        sb.AppendLine(lu == null ? $"libusb      : OK ({LibUsb.LibraryPath})" : $"libusb      : {lu}");
        if (OperatingSystem.IsLinux())
        {
            sb.AppendLine($"udev rule   : {(PlatformUtil.UdevRulePath ?? (PlatformUtil.UdevRuleInstalled() ? "a rule for 1a86:5512 exists" : "NOT installed — click 'Install udev rule'"))}");
            sb.AppendLine($"Running as  : {Environment.UserName}{(PlatformUtil.IsRoot ? " (root)" : "")}");
        }
        sb.AppendLine($"Programmer  : {(_prog?.IsOpen == true ? $"{_prog.Name} via {_prog.Backend} — {_prog.DriverInfo}" : "not connected")}");
        sb.AppendLine($"Settings    : {AppSettings.Dir}");
        sb.AppendLine($"Backups     : {_set.BackupFolder}");
        sb.AppendLine();
        if (OperatingSystem.IsWindows())
        {
            sb.AppendLine("Driver sources:");
            sb.AppendLine("  • WCH CH341PAR (parallel/SPI/I2C) driver — official from wch-ic.com (installs CH341DLLA64.DLL)");
            sb.AppendLine("  • WinUSB via Zadig — select 'USB-EPP/I2C... CH341A' (1A86:5512) and install WinUSB");
        }
        else
        {
            sb.AppendLine("Linux setup:");
            sb.AppendLine("  • Debian / Ubuntu : sudo apt install libusb-1.0-0");
            sb.AppendLine("  • Fedora          : sudo dnf install libusb1");
            sb.AppendLine("  • Permissions     : 'Install udev rule' (or packaging/linux/install.sh), then re-plug the CH341A");
            sb.AppendLine("  • If a kernel module (spi-ch341 / i2c-ch341) grabs the device it is detached automatically.");
        }
        DiagOut.Text = sb.ToString();
    }

    async void InstallUdev_Click(object sender, RoutedEventArgs e)
    {
        Log("LINUX", "Installing udev rule (you will be asked for your password)…", LogLevel.Accent);
        var (ok, msg) = await PlatformUtil.InstallUdevRuleAsync();
        Log("LINUX", msg, ok ? LogLevel.Ok : LogLevel.Error);
        DiagOut.Text = msg + "\n\n" + DiagOut.Text;
        if (ok) await InfoAsync("udev rule installed", msg);
    }

    void LoadDemo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBuffer(DemoImage.Build(16 * 1024 * 1024), "demo_synthetic_16MB.bin", null, "Demo", true);
        Log("DEMO", "Synthetic demo firmware loaded into the buffer (fake data for testing the analyzer).", LogLevel.Accent);
    }

    static string AboutText() =>
        "BiosFlash Studio 2.0  —  Windows · Debian · Fedora\n" +
        "CH341A SPI / I²C BIOS programmer with firmware forensics.\n\n" +
        "Features\n" +
        "  • CH341A via the WCH vendor driver or WinUSB (Windows), libusb (Linux), plus a built-in simulator\n" +
        "  • 160+ chip profiles, JEDEC auto-detect, SFDP size check, 3/4-byte addressing, SST AAI, 1.8 V warnings\n" +
        "  • Read / Erase / Blank check / Program / Verify / Auto, smart sector programming, double-read check\n" +
        "  • Range & region operations, status-register editor and protection removal\n" +
        "  • I²C 24Cxx EEPROM read/write\n" +
        "  • ASUS / AMI capsule (.CAP) extraction and region merge with your chip dump (keeps the MAC)\n" +
        "  • Hex editor for huge images: edit, search hex/text/UTF-16, compare with file\n" +
        "  • Firmware analysis: Intel descriptor & regions, ME/CSE version, $FPT, SMBIOS/DMI, OEM key (MSDM),\n" +
        "    GbE MAC, UEFI NVRAM (VSS/NVAR), AMD PSP/AGESA, flash usage map\n" +
        "  • Repair Tools: clear NVRAM, clean ME (≤10/TXE), set GbE MAC, unlock descriptor, merge with stock firmware\n" +
        "    (repair BIOS / descriptor / ME) and transfer the board identity (MAC, NVRAM, DMI, OEM key) from the dump\n" +
        "  • Update Extractor: InsydeFlash, AMI/ASUS/UEFI capsules, Intel full images, UEFI volumes, ZIP archives\n" +
        "  • Binary tools (split/extract/resize/append), region extract/replace, auto backups, history, TXT reports\n\n" +
        "Shortcuts: F5 Detect · F6 Read · F4 Blank · F9 Program · F7 Verify · F8 Auto · Ctrl+O Open · Ctrl+S Save · Esc Cancel\n\n" +
        "Use at your own risk. Always keep a verified backup of the original firmware.\n" +
        "Click 'Scan USB devices' or 'Driver check' to diagnose connection problems.";
}
