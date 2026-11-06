using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BiosFlashStudio.Hardware;

public sealed class UsbDeviceInfo
{
    public string InstanceId { get; set; } = "";
    public ushort Vid { get; set; }
    public ushort Pid { get; set; }
    /// <summary>Windows: service (driver) name. Linux: kernel driver bound to interface 0.</summary>
    public string Service { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Present { get; set; }
    /// <summary>Linux: /dev/bus/usb/BBB/DDD node.</summary>
    public string DevNode { get; set; } = "";
    /// <summary>Linux: current user can open the device node read/write.</summary>
    public bool Accessible { get; set; } = true;
    public List<Guid> InterfaceGuids { get; } = new();

    public bool IsCh341Spi => Vid == 0x1A86 && Pid == 0x5512;
    public bool IsCh341Uart => Vid == 0x1A86 && (Pid == 0x5523 || Pid == 0x7523);
    public bool IsWinUsb => Service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase);
    /// <summary>WCH serial (UART) driver, e.g. CH341SER / CH341SER_A64 — wrong driver for programming.</summary>
    public bool IsSerialDriver => Service.Contains("SER", StringComparison.OrdinalIgnoreCase) || Service.Equals("usbser", StringComparison.OrdinalIgnoreCase);
    /// <summary>WCH parallel/SPI driver (CH341PAR), which provides CH341DLL.</summary>
    public bool IsWchDriver => Service.StartsWith("CH341", StringComparison.OrdinalIgnoreCase) && !IsSerialDriver;

    public override string ToString()
    {
        var drv = Service == "" ? "(none)" : Service;
        if (DevNode != "")
            return $"{Vid:x4}:{Pid:x4}  {Description}\n    node={DevNode}  access={(Accessible ? "OK" : "DENIED")}  kernel driver={drv}\n    {InstanceId}";
        return $"VID_{Vid:X4} PID_{Pid:X4}  {(Present ? "PRESENT" : "not present")}  driver={drv}  {Description}\n    {InstanceId}";
    }
}

/// <summary>Finds WCH (VID 1A86) USB devices — Windows registry/Config Manager or Linux sysfs.</summary>
public static class UsbScanner
{
    public static List<UsbDeviceInfo> ScanWch()
    {
        if (OperatingSystem.IsWindows()) return ScanWindows();
        if (OperatingSystem.IsLinux()) return ScanLinux();
        return new List<UsbDeviceInfo>();
    }

    // ------------------------------------------------------------------ Linux
    static string ReadSys(string dir, string file)
    {
        try { var p = Path.Combine(dir, file); return File.Exists(p) ? File.ReadAllText(p).Trim() : ""; }
        catch { return ""; }
    }

    /// <summary>Opening a usbfs node does not claim the device; it only tests the udev permissions.</summary>
    static bool CanOpenRw(string node)
    {
        try { using var h = File.OpenHandle(node, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch { return true; }
    }

    static List<UsbDeviceInfo> ScanLinux()
    {
        var list = new List<UsbDeviceInfo>();
        const string root = "/sys/bus/usb/devices";
        try
        {
            if (!Directory.Exists(root)) return list;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.Contains(':')) continue;
                if (!ReadSys(dir, "idVendor").Equals("1a86", StringComparison.OrdinalIgnoreCase)) continue;
                ushort.TryParse(ReadSys(dir, "idProduct"), System.Globalization.NumberStyles.HexNumber, null, out var pid);
                int.TryParse(ReadSys(dir, "busnum"), out var bus);
                int.TryParse(ReadSys(dir, "devnum"), out var dev);
                var node = $"/dev/bus/usb/{bus:D3}/{dev:D3}";
                string drv = "";
                try
                {
                    var link = new DirectoryInfo(Path.Combine(root, name + ":1.0", "driver")).LinkTarget;
                    if (!string.IsNullOrEmpty(link)) drv = Path.GetFileName(link);
                }
                catch { }
                bool acc = CanOpenRw(node);
                var prod = ReadSys(dir, "product");
                var mfr = ReadSys(dir, "manufacturer");
                list.Add(new UsbDeviceInfo
                {
                    InstanceId = $"usb {name}",
                    Vid = 0x1A86,
                    Pid = pid,
                    Service = drv,
                    Description = string.Join(" ", new[] { mfr, prod }.Where(s => s != "")),
                    Present = true,
                    DevNode = node,
                    Accessible = acc,
                });
            }
        }
        catch { }
        return list;
    }

    // ------------------------------------------------------------------ Windows
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid guid, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid guid, string deviceId, char[] buffer, uint len, uint flags);

    static List<UsbDeviceInfo> ScanWindows()
    {
        var list = new List<UsbDeviceInfo>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return list;
            foreach (var vp in usb.GetSubKeyNames())
            {
                if (!vp.StartsWith("VID_1A86", StringComparison.OrdinalIgnoreCase)) continue;
                if (vp.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue;
                using var vpKey = usb.OpenSubKey(vp);
                if (vpKey == null) continue;
                ushort vid = 0x1A86, pid = 0;
                var pidIdx = vp.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
                if (pidIdx >= 0 && vp.Length >= pidIdx + 8)
                    ushort.TryParse(vp.Substring(pidIdx + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid);
                foreach (var inst in vpKey.GetSubKeyNames())
                {
                    using var ik = vpKey.OpenSubKey(inst);
                    if (ik == null) continue;
                    var info = new UsbDeviceInfo
                    {
                        InstanceId = $@"USB\{vp}\{inst}",
                        Vid = vid,
                        Pid = pid,
                        Service = ik.GetValue("Service") as string ?? "",
                        Description = CleanDesc(ik.GetValue("FriendlyName") as string ?? ik.GetValue("DeviceDesc") as string ?? ""),
                    };
                    info.Present = CM_Locate_DevNodeW(out _, info.InstanceId, 0) == 0;
                    using (var dp = ik.OpenSubKey("Device Parameters"))
                    {
                        if (dp != null)
                        {
                            if (dp.GetValue("DeviceInterfaceGUIDs") is string[] arr)
                                foreach (var g in arr) if (Guid.TryParse(g, out var gg)) info.InterfaceGuids.Add(gg);
                            if (dp.GetValue("DeviceInterfaceGUID") is string s && Guid.TryParse(s, out var g2))
                                info.InterfaceGuids.Add(g2);
                        }
                    }
                    list.Add(info);
                }
            }
        }
        catch { }
        return list;
    }

    static string CleanDesc(string s)
    {
        // "@oem12.inf,%desc%;USB-SERIAL CH340" -> "USB-SERIAL CH340"
        var i = s.LastIndexOf(';');
        return i >= 0 ? s[(i + 1)..] : s;
    }

    public static string GetInterfacePath(UsbDeviceInfo dev)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var g0 in dev.InterfaceGuids)
        {
            var g = g0;
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref g, dev.InstanceId, 0) != 0 || len <= 1) continue;
            var buf = new char[len];
            if (CM_Get_Device_Interface_ListW(ref g, dev.InstanceId, buf, len, 0) != 0) continue;
            var path = new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(path)) return path;
        }
        return null;
    }
}
