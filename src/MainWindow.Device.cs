using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BiosFlashStudio.Ui;
using BiosFlashStudio.Core;
using BiosFlashStudio.Flash;
using BiosFlashStudio.Hardware;

namespace BiosFlashStudio;

public partial class MainWindow
{
    IProgrammer _prog;
    CancellationTokenSource _cts;
    bool _busy;
    BackendKind _backendKind = BackendKind.Auto;
    bool _lastCancelled;

    // =====================================================================  connection
    BackendKind CurrentBackend() => (BackendCombo.SelectedItem as ComboBoxItem)?.Tag is BackendKind k ? k : BackendKind.Auto;

    void BackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || !_loaded) return;
        _set.Backend = CurrentBackend().ToString();
        UpdateHardwareCard();
        if (_prog != null && !_busy)
        {
            try { _prog.Close(); } catch { }
            _prog = null;
            UpdateHardwareCard();
            Log("HARDWARE", "Backend changed — programmer disconnected.");
        }
    }

    IProgrammer EnsureProgrammer()
    {
        if (_prog != null && _prog.IsOpen) return _prog;
        Log("HARDWARE", $"Opening programmer (backend: {_backendKind})…");
        var p = ProgrammerFactory.Create(_backendKind, m => Log("USB", m));
        p.Open();
        p.SetSpeed(_set.SpeedIndex);
        _prog = p;
        Dispatcher.UIThread.Invoke(UpdateHardwareCard);
        Log("HARDWARE", $"{p.Name} connected via {p.Backend} ({p.DriverInfo})", LogLevel.Ok);
        return p;
    }

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_prog?.IsOpen == true)
        {
            try { _prog.Close(); } catch { }
            _prog = null;
            UpdateHardwareCard();
            Log("HARDWARE", "Programmer disconnected.");
            return;
        }
        bool ok = await RunOp("CONNECT", ct => { EnsureProgrammer(); return $"{_prog.Name} ready"; }, history: false);
        if (!ok && OperatingSystem.IsLinux() && UsbScanner.ScanWch().Any(d => d.IsCh341Spi && !d.Accessible))
        {
            _driverHelpShown = false; // explicit click: always offer the fix again
            await OfferUdevInstallAsync();
        }
    }

    void UpdateHardwareCard()
    {
        bool on = _prog?.IsOpen == true;
        var key = on ? "Good" : "Bad";

        ConnectText.Text = on ? "Disconnect" : "Connect";
        SetPill(HwPill, HwPillText, on ? "ONLINE" : "OFFLINE", key);
        HwInterface.Text = on ? $"{_prog.Name} · USB 2.0" : "CH341A · not connected";
        HwBackend.Text = on ? _prog.Backend : $"{CurrentBackend()} (idle)";
        HwDriver.Text = on ? _prog.DriverInfo : "—";
        HwDevice.Text = on ? (string.IsNullOrEmpty(_prog.DeviceId) ? "#0" : _prog.DeviceId) : "—";
        ToolTip.SetTip(HwDevice, HwDevice.Text);
        ToolTip.SetTip(HwDriver, HwDriver.Text);
        string[] sp = { "Low", "Standard", "Fast", "High" };
        HwSpeed.Text = $"{sp[Math.Clamp(_set.SpeedIndex, 0, 3)]} (mode {_set.SpeedIndex})";
    }

    // =====================================================================  operation runner
    void SetBusy(bool busy)
    {
        foreach (var b in new[] { DetectBtn, ReadBtn, EraseBtn, BlankBtn, ProgramBtn, VerifyBtn, AutoBtn, ConnectBtn })
            b.IsEnabled = !busy;
        ModeCombo.IsEnabled = ChipCombo.IsEnabled = BackendCombo.IsEnabled = !busy;
        CancelBtn.IsVisible = OpStats.IsVisible = busy;
        Hex.ReadOnly = busy;
        SetStatus(busy ? "Working…" : "Ready", busy ? LogLevel.Accent : LogLevel.Ok);
    }

    /// <summary>Colours the operation line by outcome. state: 0 running, 1 ok, 2 fail, 3 cancelled</summary>
    void SetResult(int state)
    {
        OpTitle.Res(TextBlock.ForegroundProperty, state switch { 1 => "Good", 2 => "Bad", 3 => "Warn", _ => "Info" });
        OpDetail.Res(TextBlock.ForegroundProperty, state is 2 or 3 ? "Txt" : "TxtDim");
    }

    void UpdateProgressUi()
    {
        long total = _progress.Total, done = _progress.Done;
        double pct = total > 0 ? Math.Min(1.0, done / (double)total) : 0;
        var el = _opWatch.Elapsed;
        OpBar.Value = pct * 1000;
        OpPercent.Text = $"{pct * 100:0.0}%";
        OpBytes.Text = $"{ChipInfo.FormatSize(done)} / {ChipInfo.FormatSize(total)}";
        double speed = el.TotalSeconds > 0.2 ? done / el.TotalSeconds : 0;
        OpSpeed.Text = speed > 0 ? $"{speed / 1024:0.0} KB/s" : "—";
        OpElapsed.Text = el.ToString(@"mm\:ss");
        OpRemain.Text = speed > 0 && total > done ? TimeSpan.FromSeconds((total - done) / speed).ToString(@"mm\:ss") : _busy ? "—" : "00:00";
        OpAddr.Text = $"0x{_progress.Address:X8}";
        if (_busy && !string.IsNullOrEmpty(_progress.Stage)) OpDetail.Text = _progress.Stage;
    }

    Action<long, long> Prog => (d, a) => _progress.Set(d, a);

    async Task<bool> RunOp(string title, Func<CancellationToken, string> work, bool history = true, Func<byte[]> crcData = null)
    {
        if (_busy) { Log("UI", "Another operation is already running.", LogLevel.Warn); return false; }
        ReadSettingsFromUi();
        _busy = true;
        _lastCancelled = false;
        SetBusy(true);
        _cts = new CancellationTokenSource();
        _progress.Reset();
        _backendKind = CurrentBackend();
        OpTitle.Text = title;
        OpDetail.Text = "Working…";
        SetResult(0);
        _opWatch.Restart();
        _uiTimer.Start();
        Log("OPERATION", $"Operation started: {title}", LogLevel.Accent);

        string msg = null, err = null;
        bool ok = false;
        try
        {
            msg = await Task.Run(() => work(_cts.Token));
            ok = true;
        }
        catch (OperationCanceledException) { err = "Cancelled by user"; _lastCancelled = true; }
        catch (Exception ex)
        {
            err = ex.Message;
            if (ex is ProgrammerException && ex.Message.Contains("failed", StringComparison.OrdinalIgnoreCase) && _prog != null && _prog is not SimulatorProgrammer
                && (ex.Message.Contains("USB") || ex.Message.Contains("StreamSPI4")))
            {
                try { _prog.Close(); } catch { }
                _prog = null;
            }
        }
        finally
        {
            _opWatch.Stop();
            _uiTimer.Stop();
            UpdateProgressUi();
            _busy = false;
            SetBusy(false);
            UpdateHardwareCard();
        }
        var el = _opWatch.Elapsed;
        if (ok)
        {
            if (_progress.Total > 0) { OpBar.Value = 1000; OpPercent.Text = "100.0%"; OpRemain.Text = "00:00"; }
            OpTitle.Text = "✓ " + title + " COMPLETE";
            OpDetail.Text = msg;
            SetResult(1);
            Log("OPERATION", $"Operation completed: {title} — {msg}", LogLevel.Ok);
            SetStatus(title + " complete");
        }
        else
        {
            OpTitle.Text = (_lastCancelled ? "■ " : "✗ ") + title + (_lastCancelled ? " CANCELLED" : " FAILED");
            OpDetail.Text = err;
            SetResult(_lastCancelled ? 3 : 2);
            Log("OPERATION", $"{title} {(_lastCancelled ? "cancelled" : "FAILED")}: {err}", _lastCancelled ? LogLevel.Warn : LogLevel.Error);
            SetStatus(err, _lastCancelled ? LogLevel.Warn : LogLevel.Error);
        }
        if (history) AddHistory(title, ok ? "OK" : _lastCancelled ? "Cancelled" : "FAILED", el, ok ? msg : err, ok ? crcData?.Invoke() : null);
        return ok;
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) return;
        _cts?.Cancel();
        Log("OPERATION", "Cancellation requested…", LogLevel.Warn);
    }

    async Task<bool> ConfirmAsync(string text)
    {
        if (!_set.ConfirmDestructive) return true;
        return await AskAsync("Confirm", text, DialogKind.Warning, "Continue", "Cancel");
    }

    SpiFlash NewFlash(IProgrammer p, ChipInfo chip) => new(p, chip) { Allow4ByteMode = _set.Use4ByteMode, Log = m => Log("SPIFLASH", m, LogLevel.Warn) };

    bool RequireChip()
    {
        if (_chip == null || _chip.Size <= 0)
        {
            Log("CHIP", "No chip profile selected (or size unknown). Run Detect or choose a chip.", LogLevel.Warn);
            return false;
        }
        if (_chip.Is18V && !_chipDetected)
            Log("CHIP", $"{_chip.Name} is a 1.8 V part — make sure a 1.8 V adapter is fitted.", LogLevel.Warn);
        return true;
    }

    // =====================================================================  detect
    async void Detect_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    async Task DetectAsync()
    {
        if (_busy) return;
        var cur = _chip;
        bool i2c = IsI2c;
        ChipInfo found = null;
        await RunOp("DETECT", ct =>
        {
            var p = EnsureProgrammer();
            if (i2c)
            {
                if (!p.SupportsI2c) throw new ProgrammerException("This backend has no I²C support — use the WCH driver backend.");
                var tmp = new byte[16];
                p.I2cRead(cur.EepromId, 0, tmp, 0, 16);
                Log("I2C", $"EEPROM ACK at address 0x50 — first bytes: {Convert.ToHexString(tmp)}", LogLevel.Ok);
                found = cur;
                return $"{cur.Name} responded on the I²C bus";
            }
            var f = NewFlash(p, cur ?? ChipDatabase.SpiChips.First());
            Log("SPIFLASH", "Initializing SPI bus & querying JEDEC ID…", LogLevel.Accent);
            uint id = f.ReadJedecId();
            Log("SPIFLASH", $"JEDEC ID Read (Opcode 0x9F): {id >> 16:X2} {(id >> 8) & 0xFF:X2} {id & 0xFF:X2}");
            if (id == 0 || id == 0xFFFFFF)
                throw new ProgrammerException($"No chip detected (JEDEC ID {(id == 0 ? "00 00 00" : "FF FF FF")}). Check clip orientation (pin 1), contact and power.");
            uint id2 = f.ReadJedecId();
            if (id2 != id) Log("SPIFLASH", $"Unstable JEDEC ID ({id:X6} vs {id2:X6}) — reseat the clip!", LogLevel.Warn);
            long sfdp = f.ReadSfdpSize();
            if (sfdp > 0) Log("SPIFLASH", $"SFDP present — density {ChipInfo.FormatSize(sfdp)}");
            var matches = ChipDatabase.FindById(id);
            ChipInfo chip;
            if (matches.Count == 0)
            {
                chip = ChipDatabase.Generic(id, sfdp);
                Log("CHIP", $"JEDEC ID {id:X6} not in database — using generic profile ({chip.SizeText}, {chip.Notes}).", LogLevel.Warn);
                if (chip.Size == 0) throw new ProgrammerException($"Unknown chip {id:X6} and its size could not be determined. Select a profile manually.");
            }
            else
            {
                if (matches.Count > 1)
                    Log("CHIP", $"Multiple database profiles match JEDEC ID {id:X6} ({string.Join(", ", matches.Select(m => m.Name))})", LogLevel.Warn);
                chip = cur != null && matches.Contains(cur) ? cur : matches[0];
            }
            if (sfdp > 0 && sfdp != chip.Size) Log("CHIP", $"SFDP size {ChipInfo.FormatSize(sfdp)} differs from profile size {chip.SizeText}!", LogLevel.Warn);
            Log("CHIP", $"PROFILE: {chip.Manufacturer.ToUpperInvariant()} {chip.Name} ({chip.SizeText}, {chip.Voltage})", LogLevel.Accent);
            if (chip.Is18V) Log("CHIP", "1.8 V device — a 1.8 V adapter is REQUIRED.", LogLevel.Warn);
            var sr = f.ReadSr1();
            if ((sr & 0x9C) != 0) Log("CHIP", $"Status register SR1=0x{sr:X2}: block protection is active (will be cleared before writing).", LogLevel.Warn);
            found = chip;
            return $"{chip.Manufacturer} {chip.Name} • {chip.SizeText}";
        }, history: false);
        if (found != null) SelectChip(found, true);
    }

    // =====================================================================  read
    async void Read_Click(object sender, RoutedEventArgs e) => await ReadAsync();

    async Task ReadAsync()
    {
        if (_busy || !RequireChip()) return;
        var chip = _chip;
        bool i2c = IsI2c, dbl = SetDoubleRead.IsChecked == true;
        byte[] result = null;
        string warn = null;
        bool ok = await RunOp("READ", ct =>
        {
            var p = EnsureProgrammer();
            if (i2c)
            {
                _progress.Begin("Reading EEPROM", chip.Size);
                result = new I2cEeprom(p, chip).Read(ct, Prog);
            }
            else
            {
                var f = NewFlash(p, chip);
                f.Begin();
                try
                {
                    Log("SPIFLASH", $"Reading {chip.Size:N0} bytes from 0x00000000…");
                    _progress.Begin("Reading flash memory", chip.Size);
                    result = f.Read(0, chip.Size, ct, Prog);
                    if (dbl)
                    {
                        _progress.Begin("Second pass — comparing", chip.Size);
                        var v = f.Verify(0, result, 0, chip.Size, ct, Prog);
                        if (!v.Ok)
                        {
                            warn = $"Second read differs in {v.Mismatches:N0} bytes (first at 0x{v.FirstAddresses[0]:X8}) — unreliable contact!";
                            Log("SPIFLASH", warn, LogLevel.Error);
                        }
                        else Log("SPIFLASH", "Second read pass identical — dump is consistent.", LogLevel.Ok);
                    }
                }
                finally { f.End(); }
            }
            double secs = _opWatch.Elapsed.TotalSeconds;
            Log("FLASH", $"Read completed: {result.Length:N0} bytes in {secs:0.00}s ({result.Length / 1024.0 / Math.Max(0.01, secs):0.0} KB/s), CRC32 0x{Hashes.Crc32(result):X8}", LogLevel.Ok);
            if (SpiFlash.IsBlank(result, 0, result.Length)) Log("FLASH", "Chip contents are entirely 0xFF (blank).", LogLevel.Warn);
            return warn ?? $"Successfully read {ChipInfo.FormatSize(result.Length)} from {(i2c ? "EEPROM" : "flash memory")}.";
        }, crcData: () => result);
        if (!ok || result == null) return;
        string name = $"{chip.Name.Split('/')[0]}_read_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
        SetBuffer(result, name, null, "Chip read", true);
        if (warn != null) { OpTitle.Text = "✗ READ UNRELIABLE"; OpDetail.Text = warn; SetResult(2); }
        if (_set.AutoBackup)
        {
            try
            {
                Directory.CreateDirectory(_set.BackupFolder);
                var path = Path.Combine(_set.BackupFolder, $"{chip.Name.Split('/')[0]}_{DateTime.Now:yyyyMMdd_HHmmss}_{Hashes.Crc32(result):X8}.bin");
                await File.WriteAllBytesAsync(path, result);
                Log("BACKUP", "Auto-backup saved: " + path, LogLevel.Ok);
            }
            catch (Exception ex) { Log("BACKUP", "Auto-backup failed: " + ex.Message, LogLevel.Warn); }
        }
    }

    // =====================================================================  erase / blank
    string DoErase(SpiFlash f, ChipInfo chip, CancellationToken ct, string stagePrefix = "")
    {
        if (_set.UnprotectBeforeWrite) f.Unprotect();
        switch (_set.EraseMode)
        {
            case "Block":
                _progress.Begin(stagePrefix + "Erasing (64 KB blocks)", chip.Size);
                f.EraseRange(0, chip.Size, false, ct, Prog);
                return "block erase";
            case "Sector":
                _progress.Begin(stagePrefix + "Erasing (4 KB sectors)", chip.Size);
                f.EraseRange(0, chip.Size, true, ct, Prog);
                return "sector erase";
            default:
                _progress.Begin(stagePrefix + "Chip erase in progress (C7h)…", chip.Size);
                f.EraseChip(ct, fr => _progress.Set((long)(fr * chip.Size), (long)(fr * chip.Size)));
                return "chip erase";
        }
    }

    async void Erase_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !RequireChip()) return;
        if (!await ConfirmAsync($"ERASE the entire {_chip.Name} ({_chip.SizeText})?\n\nAll data on the chip will be lost. Make sure you saved a backup.")) return;
        var chip = _chip;
        bool i2c = IsI2c;
        await RunOp("ERASE", ct =>
        {
            var p = EnsureProgrammer();
            if (i2c)
            {
                var ff = new byte[chip.Size];
                Array.Fill(ff, (byte)0xFF);
                _progress.Begin("Filling EEPROM with 0xFF", chip.Size);
                new I2cEeprom(p, chip).Write(ff, ct, Prog);
                return "EEPROM filled with 0xFF";
            }
            var f = NewFlash(p, chip);
            f.Begin();
            try { var how = DoErase(f, chip, ct); return $"Chip erased ({how})."; }
            finally { f.End(); }
        });
    }

    async void Blank_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !RequireChip()) return;
        var chip = _chip;
        bool i2c = IsI2c;
        await RunOp("BLANK CHECK", ct =>
        {
            var p = EnsureProgrammer();
            var ff = new byte[chip.Size];
            Array.Fill(ff, (byte)0xFF);
            VerifyResult v;
            _progress.Begin("Blank checking", chip.Size);
            if (i2c)
            {
                var d = new I2cEeprom(p, chip).Read(ct, Prog);
                v = Compare(d, ff, 0);
            }
            else
            {
                var f = NewFlash(p, chip);
                f.Begin();
                try { v = f.Verify(0, ff, 0, chip.Size, ct, Prog); }
                finally { f.End(); }
            }
            if (!v.Ok) throw new ProgrammerException($"Chip is NOT blank: {v.Mismatches:N0} non-0xFF bytes, first at 0x{v.FirstAddresses[0]:X8}.");
            return "Chip is blank (all 0xFF).";
        });
    }

    static VerifyResult Compare(byte[] actual, byte[] expected, long baseAddr)
    {
        var r = new VerifyResult();
        long n = Math.Min(actual.Length, expected.Length);
        for (long i = 0; i < n; i++)
            if (actual[i] != expected[i]) { r.Mismatches++; if (r.FirstAddresses.Count < 16) r.FirstAddresses.Add(baseAddr + i); }
        return r;
    }

    // =====================================================================  program / verify / auto
    async Task<(bool ok, byte[] data, long len)> PrepareWriteAsync()
    {
        if (!RequireChip()) return (false, null, 0);
        if (_buffer.Length == 0) { Log("PROGRAM", "Buffer is empty — open a file or read a chip first.", LogLevel.Warn); return (false, null, 0); }
        if (_buffer.Length > _chip.Size)
        {
            await InfoAsync("Size mismatch", $"The buffer ({ChipInfo.FormatSize(_buffer.Length)}) is larger than the chip ({_chip.SizeText}).\n\nIf this is an ASUS/AMI .CAP file, extract it in the ASUS CAP Tool first.", DialogKind.Error);
            return (false, null, 0);
        }
        if (_buffer.Length < _chip.Size &&
            !await AskAsync("Size mismatch", $"The buffer ({ChipInfo.FormatSize(_buffer.Length)}) is smaller than the chip ({_chip.SizeText}).\nOnly the first {_buffer.Length:N0} bytes will be written. Continue?", DialogKind.Warning, "Continue", "Cancel"))
            return (false, null, 0);
        var data = (byte[])_buffer.Clone();
        return (true, data, data.Length);
    }

    VerifyResult DoVerify(IProgrammer p, SpiFlash f, ChipInfo chip, byte[] data, long len, CancellationToken ct, bool i2c, string stage = "Verifying")
    {
        _progress.Begin(stage, len);
        if (i2c) return Compare(new I2cEeprom(p, chip).Read(ct, Prog), data, 0);
        return f.Verify(0, data, 0, len, ct, Prog);
    }

    static string VerifyFail(VerifyResult v) =>
        $"VERIFY FAILED: {v.Mismatches:N0} bytes differ (first at {string.Join(", ", v.FirstAddresses.Take(4).Select(a => $"0x{a:X8}"))}).";

    async void Program_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var (pwOk, data, len) = await PrepareWriteAsync();
        if (!pwOk) return;
        bool smart = _set.SmartProgram, i2c = IsI2c, verify = _set.VerifyAfterProgram, skip = _set.SkipBlankPages;
        if (!await ConfirmAsync(smart || i2c
                ? $"Write the buffer to {_chip.Name}?\n\nChanged sectors will be erased and rewritten."
                : $"Program {_chip.Name} from the buffer?\n\nThe chip must already be ERASED (use Auto Program to erase + program + verify).")) return;
        var chip = _chip;
        await RunOp("PROGRAM", ct =>
        {
            var p = EnsureProgrammer();
            if (i2c)
            {
                _progress.Begin("Writing EEPROM", len);
                new I2cEeprom(p, chip).Write(data, ct, Prog);
                if (verify) { var v = DoVerify(p, null, chip, data, len, ct, true); if (!v.Ok) throw new ProgrammerException(VerifyFail(v)); }
                return $"EEPROM written{(verify ? " and verified" : "")}.";
            }
            var f = NewFlash(p, chip);
            f.Begin();
            try
            {
                if (_set.UnprotectBeforeWrite) f.Unprotect();
                string summary;
                if (smart)
                {
                    _progress.Begin("Smart programming (diff by 4 KB sector)", len);
                    var (sk, er, wr) = f.SmartProgram(0, data, len, ct, Prog);
                    summary = $"Smart write: {wr} sectors written, {er} erased, {sk} unchanged";
                }
                else
                {
                    _progress.Begin("Programming pages", len);
                    f.Program(0, data, 0, len, skip, ct, Prog);
                    summary = $"Programmed {ChipInfo.FormatSize(len)}";
                }
                Log("PROGRAM", summary, LogLevel.Ok);
                if (verify)
                {
                    var v = DoVerify(p, f, chip, data, len, ct, false);
                    if (!v.Ok) throw new ProgrammerException(VerifyFail(v));
                    summary += " • verified OK";
                }
                return summary + ".";
            }
            finally { f.End(); }
        }, crcData: () => data);
    }

    async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !RequireChip()) return;
        if (_buffer.Length == 0) { Log("VERIFY", "Buffer is empty.", LogLevel.Warn); return; }
        var data = (byte[])_buffer.Clone();
        long len = Math.Min(data.Length, _chip.Size);
        var chip = _chip;
        bool i2c = IsI2c;
        await RunOp("VERIFY", ct =>
        {
            var p = EnsureProgrammer();
            SpiFlash f = i2c ? null : NewFlash(p, chip);
            f?.Begin();
            try
            {
                var v = DoVerify(p, f, chip, data, len, ct, i2c);
                if (!v.Ok)
                {
                    foreach (var a in v.FirstAddresses.Take(8)) Log("VERIFY", $"  mismatch @0x{a:X8}: buffer=0x{data[a]:X2}", LogLevel.Error);
                    throw new ProgrammerException(VerifyFail(v));
                }
                return $"Chip matches buffer ({ChipInfo.FormatSize(len)}, CRC32 0x{Hashes.Crc32(data):X8}).";
            }
            finally { f?.End(); }
        }, crcData: () => data);
    }

    async void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var (pwOk, data, len) = await PrepareWriteAsync();
        if (!pwOk) return;
        if (!await ConfirmAsync($"AUTO PROGRAM {_chip.Name}:\n\n1. Remove protection\n2. Erase\n3. Blank check\n4. Program\n5. Verify\n\nAll current chip contents will be replaced by the buffer ({ChipInfo.FormatSize(len)}). Continue?")) return;
        var chip = _chip;
        bool i2c = IsI2c, skip = _set.SkipBlankPages;
        await RunOp("AUTO PROGRAM", ct =>
        {
            var p = EnsureProgrammer();
            if (i2c)
            {
                _progress.Begin("[1/2] Writing EEPROM", len);
                new I2cEeprom(p, chip).Write(data, ct, Prog);
                var v1 = DoVerify(p, null, chip, data, len, ct, true, "[2/2] Verifying");
                if (!v1.Ok) throw new ProgrammerException(VerifyFail(v1));
                return "EEPROM programmed and verified.";
            }
            var f = NewFlash(p, chip);
            f.Begin();
            try
            {
                Log("AUTO", "[1/4] Erasing…", LogLevel.Accent);
                DoErase(f, chip, ct, "[1/4] ");
                Log("AUTO", "[2/4] Blank check…", LogLevel.Accent);
                var ff = new byte[len];
                Array.Fill(ff, (byte)0xFF);
                _progress.Begin("[2/4] Blank check", len);
                var b = f.Verify(0, ff, 0, len, ct, Prog);
                if (!b.Ok) throw new ProgrammerException($"Erase failed — {b.Mismatches:N0} bytes not blank (first at 0x{b.FirstAddresses[0]:X8}). Chip may be write-protected (WP# pin / SRP).");
                Log("AUTO", "[3/4] Programming…", LogLevel.Accent);
                _progress.Begin("[3/4] Programming pages", len);
                f.Program(0, data, 0, len, skip, ct, Prog);
                Log("AUTO", "[4/4] Verifying…", LogLevel.Accent);
                var v = DoVerify(p, f, chip, data, len, ct, false, "[4/4] Verifying");
                if (!v.Ok) throw new ProgrammerException(VerifyFail(v));
                return $"Erase + program + verify OK ({ChipInfo.FormatSize(len)}, CRC32 0x{Hashes.Crc32(data):X8}).";
            }
            finally { f.End(); }
        }, crcData: () => data);
    }

    // =====================================================================  chip functions
    void FnOut(string s)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => FnOut(s)); return; }
        FuncOut.Text = (FuncOut.Text ?? "") + $"[{DateTime.Now:HH:mm:ss}] {s}\n";
        FuncOut.CaretIndex = FuncOut.Text.Length;
    }

    void FnClear_Click(object sender, RoutedEventArgs e) => FuncOut.Text = "";

    async Task SpiFn(string title, Func<SpiFlash, string> fn)
    {
        if (_busy) return;
        if (IsI2c) { Log("CHIP", "This function is only available in SPI mode.", LogLevel.Warn); return; }
        var chip = _chip ?? ChipDatabase.SpiChips.First();
        ShowPage(7);
        await RunOp(title, ct =>
        {
            var f = NewFlash(EnsureProgrammer(), chip);
            var s = fn(f);
            FnOut(s);
            return s.Split('\n')[0];
        }, history: false);
    }

    static string Bits(byte v) => string.Join("    ", Enumerable.Range(0, 8).Select(i => (v >> (7 - i) & 1).ToString()));

    async void ReadStatus_Click(object sender, RoutedEventArgs e) => await SpiFn("READ STATUS", f =>
    {
        byte s1 = f.ReadSr1(), s2 = f.ReadSr2(), s3 = f.ReadSr3();
        Dispatcher.UIThread.Invoke(() =>
        {
            Sr1Text.Text = $"0x{s1:X2}"; Sr2Text.Text = $"0x{s2:X2}"; Sr3Text.Text = $"0x{s3:X2}";
            Sr1Bits.Text = $"SRP:{s1 >> 7 & 1} SEC:{s1 >> 6 & 1} TB:{s1 >> 5 & 1} BP:{s1 >> 2 & 7} WEL:{s1 >> 1 & 1} BUSY:{s1 & 1}";
            Sr2Bits.Text = $"SUS:{s2 >> 7 & 1} CMP:{s2 >> 6 & 1} LB:{s2 >> 3 & 7} QE:{s2 >> 1 & 1} SRL:{s2 & 1}";
            ProtText.Text = (s1 & 0x1C) != 0 ? "⚠ Block protection ACTIVE" : "✓ No block protection";
            ProtText.Res(TextBlock.ForegroundProperty, (s1 & 0x1C) != 0 ? "Warn" : "Good");
        });
        return $"SR1=0x{s1:X2} SR2=0x{s2:X2} SR3=0x{s3:X2}\n  SR1 bits 7..0: {Bits(s1)}\n  SR2 bits 7..0: {Bits(s2)}";
    });

    async void Unprotect_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Clear the status-register block-protection bits?")) return;
        await SpiFn("UNPROTECT", f => { var sr = f.Unprotect(); return $"Protection removed — SR1 is now 0x{sr:X2}{((sr & 0x1C) != 0 ? " (bits still set: WP# pin held low?)" : "")}"; });
    }

    async void WriteSr1_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseNum(Sr1Write.Text, out var v) || v > 0xFF) { Log("CHIP", "Enter a hex byte for SR1.", LogLevel.Warn); return; }
        if (!await ConfirmAsync($"Write 0x{v:X2} to status register 1?")) return;
        await SpiFn("WRITE SR1", f => { f.WriteSr1((byte)v); return $"SR1 written: 0x{v:X2} → read back 0x{f.ReadSr1():X2}"; });
    }

    async void FnJedec_Click(object sender, RoutedEventArgs e) => await SpiFn("JEDEC ID", f =>
    {
        var id = f.ReadJedecId();
        var m = ChipDatabase.FindById(id);
        return $"JEDEC ID: {id:X6}  (manufacturer {ChipDatabase.VendorName((byte)(id >> 16))}, type 0x{(id >> 8) & 0xFF:X2}, capacity code 0x{id & 0xFF:X2})" +
               (m.Count > 0 ? "\n  Matches: " + string.Join(", ", m.Select(c => c.Name)) : "\n  No database match");
    });

    async void FnRes_Click(object sender, RoutedEventArgs e) => await SpiFn("RES", f => $"RES electronic signature (ABh): 0x{f.ReadRes():X2}");
    async void FnRems_Click(object sender, RoutedEventArgs e) => await SpiFn("REMS", f => { var r = f.ReadRems(); return $"REMS (90h): manufacturer 0x{r[0]:X2}, device 0x{r[1]:X2}"; });
    async void FnUid_Click(object sender, RoutedEventArgs e) => await SpiFn("UNIQUE ID", f => $"Unique ID (4Bh): {Convert.ToHexString(f.ReadUniqueId())}");

    async void FnSfdp_Click(object sender, RoutedEventArgs e) => await SpiFn("SFDP", f =>
    {
        var d = f.ReadSfdp(0, 256);
        var sb = new StringBuilder();
        bool valid = d[0] == 'S' && d[1] == 'F' && d[2] == 'D' && d[3] == 'P';
        sb.Append(valid ? $"SFDP v{d[5]}.{d[4]}, {d[6] + 1} parameter header(s), density {ChipInfo.FormatSize(f.ReadSfdpSize())}" : "No SFDP signature (chip does not support SFDP)");
        sb.Append('\n');
        for (int i = 0; i < 256; i += 16)
            sb.Append($"  {i:X4}: {BitConverter.ToString(d, i, 16).Replace('-', ' ')}\n");
        return sb.ToString().TrimEnd();
    });

    // =====================================================================  range operations
    bool GetRange(out long start, out long len)
    {
        start = len = 0;
        if (!RequireChip()) return false;
        if (!TryParseNum(RangeStart.Text, out start) || !TryParseNum(RangeLen.Text, out len) || len <= 0 || start + len > _chip.Size)
        {
            Log("RANGE", $"Invalid range (chip size is 0x{_chip.Size:X}). Values are hex; K/M suffix = decimal KB/MB.", LogLevel.Warn);
            return false;
        }
        if (IsI2c) { Log("RANGE", "Range operations are for SPI flash.", LogLevel.Warn); return false; }
        return true;
    }

    void RangeFromSel_Click(object sender, RoutedEventArgs e)
    {
        if (_buffer.Length == 0) return;
        RangeStart.Text = $"0x{Hex.SelectionStart:X8}";
        RangeLen.Text = $"0x{Hex.SelectionLength:X}";
    }

    void RegionPick_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RegionPick.SelectedItem is not ComboBoxItem { Tag: Analysis.FlashRegion r }) return;
        RangeStart.Text = $"0x{r.Start:X8}";
        RangeLen.Text = $"0x{r.Size:X}";
    }

    async void RangeRead_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !GetRange(out var start, out var len)) return;
        var chip = _chip;
        byte[] part = null;
        bool ok = await RunOp("READ RANGE", ct =>
        {
            var f = NewFlash(EnsureProgrammer(), chip);
            f.Begin();
            try { _progress.Begin($"Reading 0x{start:X8}+0x{len:X}", len); part = f.Read(start, len, ct, Prog); }
            finally { f.End(); }
            return $"Read 0x{len:X} bytes at 0x{start:X8} (CRC32 0x{Hashes.Crc32(part):X8}).";
        });
        if (!ok) return;
        if (_buffer.Length != chip.Size)
        {
            var nb = new byte[chip.Size];
            Array.Fill(nb, (byte)0xFF);
            SetBuffer(nb, $"{chip.Name.Split('/')[0]}_partial.bin", null, "Partial read", false);
        }
        Buffer.BlockCopy(part, 0, _buffer, (int)start, (int)len);
        Hex.RefreshView();
        _modCount = Hex.ModifiedCount();
        UpdateBufferInfo();
        Hex.Select(start, len);
        ShowPage(1);
    }

    async void RangeErase_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !GetRange(out var start, out var len)) return;
        long s = start & ~0xFFFL, end = (start + len + 0xFFF) & ~0xFFFL;
        if (!await ConfirmAsync($"Erase 0x{s:X8} – 0x{end - 1:X8} ({ChipInfo.FormatSize(end - s)}, 4 KB aligned)?")) return;
        var chip = _chip;
        await RunOp("ERASE RANGE", ct =>
        {
            var f = NewFlash(EnsureProgrammer(), chip);
            f.Begin();
            try
            {
                if (_set.UnprotectBeforeWrite) f.Unprotect();
                _progress.Begin("Erasing range", end - s);
                f.EraseRange(s, end - s, false, ct, Prog);
            }
            finally { f.End(); }
            return $"Erased 0x{s:X8}-0x{end - 1:X8}.";
        });
    }

    async void RangeWrite_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !GetRange(out var start, out var len)) return;
        if (_buffer.Length != _chip.Size) { Log("RANGE", "Range write needs a full-size buffer matching the chip.", LogLevel.Warn); return; }
        long s = start & ~0xFFFL, end = (start + len + 0xFFF) & ~0xFFFL;
        if (!await ConfirmAsync($"Write buffer range 0x{s:X8} – 0x{end - 1:X8} ({ChipInfo.FormatSize(end - s)}) to the chip?\nOnly differing 4 KB sectors are erased/rewritten.")) return;
        var chip = _chip;
        var slice = _buffer[(int)s..(int)end];
        bool verify = _set.VerifyAfterProgram;
        await RunOp("WRITE RANGE", ct =>
        {
            var f = NewFlash(EnsureProgrammer(), chip);
            f.Begin();
            try
            {
                if (_set.UnprotectBeforeWrite) f.Unprotect();
                _progress.Begin("Smart writing range", slice.Length);
                var (sk, er, wr) = f.SmartProgram(s, slice, slice.Length, ct, Prog);
                string m = $"Range written: {wr} sectors written, {er} erased, {sk} unchanged";
                if (verify)
                {
                    _progress.Begin("Verifying range", slice.Length);
                    var v = f.Verify(s, slice, 0, slice.Length, ct, Prog);
                    if (!v.Ok) throw new ProgrammerException(VerifyFail(v));
                    m += " • verified";
                }
                return m + ".";
            }
            finally { f.End(); }
        });
    }

    async void RangeVerify_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !GetRange(out var start, out var len)) return;
        if (_buffer.Length < start + len) { Log("RANGE", "Buffer does not cover that range.", LogLevel.Warn); return; }
        var chip = _chip;
        var data = (byte[])_buffer.Clone();
        await RunOp("VERIFY RANGE", ct =>
        {
            var f = NewFlash(EnsureProgrammer(), chip);
            f.Begin();
            try
            {
                _progress.Begin("Verifying range", len);
                var v = f.Verify(start, data, start, len, ct, Prog);
                if (!v.Ok) throw new ProgrammerException(VerifyFail(v));
            }
            finally { f.End(); }
            return $"Range 0x{start:X8}+0x{len:X} matches buffer.";
        });
    }

    async void Bench_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || IsI2c || !RequireChip()) return;
        var chip = _chip;
        string res = null;
        await RunOp("BENCHMARK", ct =>
        {
            var f = NewFlash(EnsureProgrammer(), chip);
            long n = Math.Min(chip.Size, 512 * 1024);
            _progress.Begin("Reading 512 KB", n);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            f.Read(0, n, ct, Prog);
            sw.Stop();
            double kbs = n / 1024.0 / sw.Elapsed.TotalSeconds;
            res = $"Read speed: {kbs:0.0} KB/s ({n / 1024} KB in {sw.Elapsed.TotalSeconds:0.00}s) — full {chip.SizeText} read ≈ {TimeSpan.FromSeconds(chip.Size / 1024.0 / kbs):mm\\:ss}";
            return res;
        }, history: false);
        if (res != null) DiagOut.Text = res + "\r\n\r\n" + DiagOut.Text;
    }
}
