using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BiosFlashStudio.Platform;

public static class PlatformUtil
{
    public const string UdevRuleName = "60-biosflash-ch341a.rules";

    public const string UdevRule =
        "# BiosFlash Studio - give the logged-in user access to CH341A programmers\n" +
        "# CH341A in SPI/I2C (EPP/MEM) mode\n" +
        "SUBSYSTEM==\"usb\", ATTRS{idVendor}==\"1a86\", ATTRS{idProduct}==\"5512\", MODE=\"0666\", TAG+=\"uaccess\"\n";

    static readonly string[] UdevDirs = { "/etc/udev/rules.d", "/usr/lib/udev/rules.d", "/lib/udev/rules.d" };

    public static string OsName
    {
        get
        {
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    foreach (var l in File.ReadAllLines("/etc/os-release"))
                        if (l.StartsWith("PRETTY_NAME=")) return l[12..].Trim('"') + $" ({RuntimeInformation.OSArchitecture})";
                }
                catch { }
                return "Linux " + Environment.OSVersion.Version;
            }
            return RuntimeInformation.OSDescription + $" ({RuntimeInformation.OSArchitecture})";
        }
    }

    public static string UdevRulePath => UdevDirs.Select(d => Path.Combine(d, UdevRuleName)).FirstOrDefault(File.Exists);

    /// <summary>Is any udev rule for 1a86:5512 installed (ours or another tool's, e.g. flashrom/ch341eeprom)?</summary>
    public static bool UdevRuleInstalled()
    {
        if (UdevRulePath != null) return true;
        foreach (var d in UdevDirs)
        {
            try
            {
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.GetFiles(d, "*.rules"))
                {
                    var t = File.ReadAllText(f);
                    if (t.Contains("1a86", StringComparison.OrdinalIgnoreCase) && t.Contains("5512")) return true;
                }
            }
            catch { }
        }
        return false;
    }

    /// <summary>Installs the udev rule through pkexec (graphical sudo prompt). Returns (ok, message).</summary>
    public static async Task<(bool ok, string msg)> InstallUdevRuleAsync()
    {
        if (!OperatingSystem.IsLinux()) return (false, "udev rules are only used on Linux.");
        var tmp = Path.Combine(Path.GetTempPath(), UdevRuleName);
        await File.WriteAllTextAsync(tmp, UdevRule);
        string script = $"install -m 0644 '{tmp}' /etc/udev/rules.d/{UdevRuleName} && udevadm control --reload-rules && udevadm trigger --subsystem-match=usb --attr-match=idVendor=1a86";
        foreach (var elevate in new[] { "pkexec", "sudo" })
        {
            try
            {
                var psi = new ProcessStartInfo(elevate) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
                if (elevate == "sudo") psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add("sh");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(script);
                using var p = Process.Start(psi);
                if (p == null) continue;
                var err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode == 0) return (true, $"Installed /etc/udev/rules.d/{UdevRuleName}. Re-plug the CH341A programmer.");
                if (elevate == "pkexec" && (p.ExitCode == 126 || p.ExitCode == 127)) return (false, "Authorization was cancelled.");
                if (elevate == "sudo") return (false, "Could not install the rule automatically: " + err.Trim());
            }
            catch { }
        }
        return (false, $"Could not elevate. Run manually:\n  sudo cp {tmp} /etc/udev/rules.d/ && sudo udevadm control --reload-rules && sudo udevadm trigger");
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", path);
            else
            {
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
        }
        catch { }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
            {
                var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(url);
                Process.Start(psi);
            }
        }
        catch { }
    }

    public static bool IsRoot => !OperatingSystem.IsWindows() && Environment.UserName == "root";
}
