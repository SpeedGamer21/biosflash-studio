namespace BiosFlashStudio.Hardware;

public static class ProgrammerFactory
{
    /// <summary>Backends offered on the current operating system (tag, display text).</summary>
    public static IReadOnlyList<(BackendKind kind, string text)> Available()
    {
        var l = new List<(BackendKind, string)> { (BackendKind.Auto, "Auto detect") };
        if (OperatingSystem.IsWindows())
        {
            l.Add((BackendKind.Ch341Dll, "CH341A · WCH driver (DLL)"));
            l.Add((BackendKind.WinUsb, "CH341A · WinUSB (Zadig)"));
            l.Add((BackendKind.LibUsb, "CH341A · libusb-1.0.dll"));
        }
        else l.Add((BackendKind.LibUsb, "CH341A · libusb"));
        l.Add((BackendKind.Simulator, "Simulator (no hardware)"));
        return l;
    }

    public static IProgrammer Create(BackendKind kind, Action<string> log)
    {
        if (kind == BackendKind.Simulator) return SimulatorProgrammer.Shared;

        var devs = UsbScanner.ScanWch();
        var present = devs.Where(d => d.Present).ToList();
        foreach (var d in present)
            log?.Invoke($"USB device: {d.Vid:X4}:{d.Pid:X4} driver={(d.Service == "" ? "none" : d.Service)}{(d.DevNode != "" ? $" node={d.DevNode}{(d.Accessible ? "" : " (NO ACCESS)")}" : "")} {d.Description}");
        var spi = present.FirstOrDefault(d => d.IsCh341Spi);
        string uartMsg = "A CH340/CH341 is connected in UART (serial) mode (PID 5523/7523). Move the programmer jumper to the SPI/I²C position (pins 1-2) and re-plug it.";

        if (!OperatingSystem.IsWindows())
        {
            if (kind is BackendKind.Ch341Dll or BackendKind.WinUsb)
                throw new ProgrammerException("The WCH DLL and WinUSB backends exist only on Windows. Use the libusb backend.");
            if (spi == null)
            {
                if (present.Any(d => d.IsCh341Uart)) throw new ProgrammerException(uartMsg);
                if (OperatingSystem.IsLinux()) throw new ProgrammerException("No CH341A (1a86:5512) found on the USB bus. Check the cable/jumper, then run 'lsusb'.");
            }
            if (spi != null && !spi.Accessible)
                throw new ProgrammerException($"Permission denied on {spi.DevNode}. Install the udev rule (Diagnostics → Install udev rule) and re-plug the programmer — or run once with sudo.");
            return new Ch341LibUsbProgrammer(spi) { Log = log };
        }

        if (kind == BackendKind.LibUsb) return new Ch341LibUsbProgrammer(spi) { Log = log };
        if (kind == BackendKind.WinUsb)
        {
            if (spi == null) throw new ProgrammerException("No CH341A (1A86:5512) is plugged in.");
            if (!spi.IsWinUsb) throw new ProgrammerException($"CH341A uses driver '{spi.Service}', not WinUSB. Install WinUSB with Zadig or choose the WCH driver backend.");
            return new Ch341WinUsbProgrammer(spi) { Log = log };
        }
        if (kind == BackendKind.Ch341Dll) return new Ch341DllProgrammer();

        // Auto (Windows)
        if (spi != null && spi.IsWinUsb) return new Ch341WinUsbProgrammer(spi) { Log = log };
        if (spi == null && present.Any(d => d.IsCh341Uart)) throw new ProgrammerException(uartMsg);
        if (spi == null && devs.Count > 0 && Ch341DllProgrammer.TryLoadLibrary() != null)
            throw new ProgrammerException("No CH341A programmer is connected.");
        if (spi != null && spi.Service == "")
            throw new ProgrammerException("CH341A found but no driver is installed. Install the WCH CH341PAR driver (recommended) or WinUSB via Zadig.");
        return new Ch341DllProgrammer();
    }
}
