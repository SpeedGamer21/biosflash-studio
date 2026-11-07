using Avalonia.Interactivity;
using Avalonia.Threading;
using BiosFlashStudio.Core;
using BiosFlashStudio.Hardware;
using BiosFlashStudio.Platform;
using BiosFlashStudio.Ui;

namespace BiosFlashStudio;

/// <summary>
/// USB hot-plug watcher: polls the WCH devices (sysfs on Linux, the registry/Config Manager on Windows),
/// connects automatically when a usable CH341A appears, drops the connection when it is unplugged and
/// explains what is wrong (UART jumper, missing/serial driver, udev permissions) when it cannot be used.
/// </summary>
public partial class MainWindow
{
    const string WchDriverUrl = "https://www.wch-ic.com/downloads/CH341PAR_EXE.html";
    const string ZadigUrl = "https://zadig.akeo.ie/";

    DispatcherTimer _usbTimer;
    string _usbSig;
    bool _usbScanning, _driverHelpShown;

    void StartUsbWatch()
    {
        _usbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _usbTimer.Tick += async (_, _) => await UsbWatchTickAsync();
        _usbTimer.Start();
        _ = UsbWatchTickAsync();
    }

    async Task UsbWatchTickAsync()
    {
        if (_usbScanning || _busy || CurrentBackend() == BackendKind.Simulator) return;
        _usbScanning = true;
        List<UsbDeviceInfo> devs;
        try { devs = await Task.Run(() => UsbScanner.ScanWch().Where(d => d.Present).ToList()); }
        catch { return; }
        finally { _usbScanning = false; }

        var sig = string.Join("|", devs.Select(d => $"{d.InstanceId}/{d.Pid:X4}/{d.Service}/{d.Accessible}").OrderBy(s => s));
        if (sig == _usbSig) return;
        bool initial = _usbSig == null;
        _usbSig = sig;
        if (_busy) { _usbSig = null; return; } // re-evaluate once the running operation finishes
        await OnUsbChangedAsync(devs, initial);
    }

    /// <summary>Why a present CH341A in SPI mode cannot be opened, or null when it is usable.</summary>
    static string Ch341Problem(UsbDeviceInfo d)
    {
        if (OperatingSystem.IsWindows())
        {
            if (d.IsWinUsb || d.IsWchDriver) return null;
            if (d.Service == "") return "CH341A plugged in, but no driver is installed";
            if (d.IsSerialDriver) return $"CH341A is bound to the serial driver {d.Service}";
            return $"CH341A uses an unknown driver '{d.Service}'";
        }
        return d.Accessible ? null : "CH341A plugged in, but permission denied — install the udev rule (Diagnostics)";
    }

    async Task OnUsbChangedAsync(List<UsbDeviceInfo> devs, bool initial)
    {
        var spi = devs.FirstOrDefault(d => d.IsCh341Spi);
        bool connected = _prog?.IsOpen == true && _prog is not SimulatorProgrammer;

        // ---- unplugged
        if (spi == null)
        {
            if (connected)
            {
                try { _prog.Close(); } catch { }
                _prog = null;
                UpdateHardwareCard();
                Log("USB", "CH341A unplugged — programmer disconnected.", LogLevel.Warn);
                SetStatus("Programmer unplugged", LogLevel.Warn);
            }
            if (!initial && devs.Any(d => d.IsCh341Uart))
            {
                Log("USB", "A CH340/CH341 appeared in UART (serial) mode. It cannot program chips: move the jumper to pins 1-2 (SPI/I²C) and re-plug it.", LogLevel.Warn);
                SetStatus("CH341 is in UART mode — move the jumper to SPI/I²C (pins 1-2)", LogLevel.Warn);
            }
            return;
        }

        if (connected) return; // already talking to it

        if (!initial) Log("USB", $"CH341A (1A86:5512) plugged in{(spi.Service != "" ? $" — driver {spi.Service}" : "")}.", LogLevel.Accent);

        var problem = Ch341Problem(spi);
        if (problem != null)
        {
            Log("USB", problem + ".", LogLevel.Warn);
            SetStatus(problem, LogLevel.Warn);
            if (OperatingSystem.IsWindows() && !_driverHelpShown)
            {
                _driverHelpShown = true;
                if (await AskAsync("CH341A driver needed",
                        problem + ".\n\nWindows has no built-in driver for the programmer mode. Install the WCH CH341PAR driver (recommended) " +
                        "or WinUSB with Zadig. BiosFlash Studio connects automatically as soon as a driver is installed.",
                        DialogKind.Warning, "Show driver setup", "Later"))
                {
                    ShowPage(12);
                    DriverCheck_Click(null, null);
                }
            }
            if (OperatingSystem.IsLinux()) await OfferUdevInstallAsync();
            return;
        }

        if (!_set.AutoConnect || CurrentBackend() == BackendKind.Simulator) return;

        Log("USB", "Connecting automatically…", LogLevel.Accent);
        if (_set.AutoDetectChip) await DetectAsync();
        else await RunOp("CONNECT", ct => { EnsureProgrammer(); return $"{_prog.Name} ready"; }, history: false);
    }

    /// <summary>Linux: the CH341A is present but its /dev/bus/usb node is root-only — offer to install the udev rule (once per session).</summary>
    async Task OfferUdevInstallAsync()
    {
        if (!OperatingSystem.IsLinux() || _driverHelpShown || PlatformUtil.IsRoot) return;
        _driverHelpShown = true;
        if (!await AskAsync("Allow access to the CH341A",
                "The programmer is plugged in, but your user is not allowed to open it yet.\n\n" +
                "Linux needs no driver for the CH341A — only a one-time udev rule that gives your user access to it " +
                "(you will be asked for your password). BiosFlash Studio connects automatically afterwards.",
                DialogKind.Question, "Install udev rule", "Later"))
            return;
        Log("LINUX", "Installing udev rule (you will be asked for your password)…", LogLevel.Accent);
        var (ok, msg) = await PlatformUtil.InstallUdevRuleAsync();
        Log("LINUX", msg, ok ? LogLevel.Ok : LogLevel.Error);
        if (!ok) { _driverHelpShown = false; await InfoAsync("udev rule not installed", msg, DialogKind.Warning); return; }
        _usbSig = null; // force the watcher to re-evaluate: udevadm trigger has just fixed the node permissions
    }

    void GetWchDriver_Click(object sender, RoutedEventArgs e) => PlatformUtil.OpenUrl(WchDriverUrl);
    void GetZadig_Click(object sender, RoutedEventArgs e) => PlatformUtil.OpenUrl(ZadigUrl);
}
