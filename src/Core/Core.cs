using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace BiosFlashStudio.Core;

public sealed class AppSettings
{
    public string Backend { get; set; } = "Auto";
    public int SpeedIndex { get; set; } = 1;
    public bool VerifyAfterProgram { get; set; } = true;
    public bool SmartProgram { get; set; } = false;
    public bool UnprotectBeforeWrite { get; set; } = true;
    public bool DoubleRead { get; set; } = false;
    public bool SkipBlankPages { get; set; } = true;
    public bool AutoAnalyze { get; set; } = true;
    public bool AutoBackup { get; set; } = true;
    public bool Use4ByteMode { get; set; } = true;
    public bool ConfirmDestructive { get; set; } = true;
    public string EraseMode { get; set; } = "Chip";
    public string BackupFolder { get; set; } = DefaultBackupFolder;
    public bool DarkTheme { get; set; } = true;
    public string LastChip { get; set; } = "";
    public string LastFolder { get; set; } = "";
    public bool ShowLog { get; set; } = false;
    public bool ShowDetails { get; set; } = false;
    /// <summary>Connect automatically when a CH341A is plugged in (or already present at start-up).</summary>
    public bool AutoConnect { get; set; } = true;
    /// <summary>Read the JEDEC ID right after an automatic connect.</summary>
    public bool AutoDetectChip { get; set; } = true;

    public static string Dir => Path.Combine(Special(Environment.SpecialFolder.ApplicationData, ".config"), "BiosFlashStudio");
    public static string DefaultBackupFolder => Path.Combine(Special(Environment.SpecialFolder.MyDocuments, "Documents"), "BiosFlash Studio", "Backups");

    /// <summary>Home directory, never empty (Linux sessions without XDG folders / $HOME return "" from GetFolderPath).</summary>
    static string Home()
    {
        var h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(h)) h = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(h)) h = Environment.UserName == "root" ? "/root" : Path.GetTempPath();
        return h;
    }

    static string Special(Environment.SpecialFolder f, string fallbackUnderHome)
    {
        var p = Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(p) || !Path.IsPathRooted(p) ? Path.Combine(Home(), fallbackUnderHome) : p;
    }
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                if (string.IsNullOrWhiteSpace(s.BackupFolder) || !Path.IsPathRooted(s.BackupFolder)) s.BackupFolder = DefaultBackupFolder;
                return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public string Operation { get; set; } = "";
    public string Chip { get; set; } = "";
    public string Result { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Size { get; set; } = "";
    public string Crc32 { get; set; } = "";
    public string Details { get; set; } = "";
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");
}

public static class HistoryStore
{
    static string FilePath => Path.Combine(AppSettings.Dir, "history.json");

    public static ObservableCollection<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return new ObservableCollection<HistoryEntry>(JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? new());
        }
        catch { }
        return new ObservableCollection<HistoryEntry>();
    }

    public static void Save(IEnumerable<HistoryEntry> items)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(items.TakeLast(1000).ToList()));
        }
        catch { }
    }
}

public enum LogLevel { Info, Ok, Warn, Error, Accent }

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public LogLevel Level { get; init; }
    public string TimeText => Time.ToString("HH:mm:ss");
    public string CategoryText => Category.Length > 9 ? Category[..9] : Category;
    public bool IsOk => Level == LogLevel.Ok;
    public bool IsWarn => Level == LogLevel.Warn;
    public bool IsError => Level == LogLevel.Error;
    public bool IsAccent => Level == LogLevel.Accent;
    public override string ToString() => $"[{TimeText}] {Category,-9} {Message}";
}

public static class Hashes
{
    static readonly uint[] Table = BuildTable();
    static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in d) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    public static uint Sum32(byte[] d)
    {
        uint s = 0;
        foreach (var b in d) s += b;
        return s;
    }

    public static string Sha256(byte[] d) => Convert.ToHexString(SHA256.HashData(d));
}
