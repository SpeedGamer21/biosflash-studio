using System.Diagnostics;

/// <summary>
/// Test progress output: timestamped section headers, PASS/FAIL lines with a running count,
/// a live progress bar for long operations (interactive consoles) and a log file you can watch.
/// </summary>
sealed class Reporter : IDisposable
{
    readonly Stopwatch _total = Stopwatch.StartNew();
    readonly StreamWriter _log;
    readonly bool _interactive = !Console.IsOutputRedirected;
    readonly object _lock = new();
    readonly int _sections;
    int _section, _pass, _fail;
    bool _barShown;

    public string LogPath { get; }
    public int Failures => _fail;

    public Reporter(int sections, string logPath)
    {
        _sections = sections;
        LogPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        _log = new StreamWriter(logPath, false) { AutoFlush = true };
        Line($"BiosFlash Studio self-test started {DateTime.Now:yyyy-MM-dd HH:mm:ss}  (log: {logPath})");
    }

    string Stamp => $"[{DateTime.Now:HH:mm:ss} +{_total.Elapsed:mm\\:ss}]";

    public void Line(string text)
    {
        lock (_lock)
        {
            ClearBar();
            Console.WriteLine(text);
            _log.WriteLine(text);
        }
    }

    /// <summary>Detail output (e.g. analyzer report) — goes to the log only.</summary>
    public void Detail(string text) { lock (_lock) _log.WriteLine(text); }

    public void Section(string name) => Line($"\n{Stamp} >> [{++_section}/{_sections}] {name}");

    public void Check(bool ok, string msg)
    {
        if (ok) _pass++; else _fail++;
        Line($"{Stamp}   {(ok ? "PASS" : "FAIL")}  {msg}   ({_pass} passed{(_fail > 0 ? $", {_fail} FAILED" : "")})");
    }

    public void Info(string msg) => Line($"{Stamp}   -     {msg}");

    /// <summary>Progress callback for SpiFlash/I2cEeprom (done bytes, address). Bar on screen, 25 % steps in the log.</summary>
    public Action<long, long> Bar(string label, long total)
    {
        var sw = Stopwatch.StartNew();
        int lastQuarter = -1;
        long lastDraw = -1000;
        return (done, _) =>
        {
            double f = total > 0 ? Math.Min(1.0, done / (double)total) : 1;
            int quarter = (int)(f * 4);
            lock (_lock)
            {
                if (quarter != lastQuarter && quarter < 4)
                {
                    lastQuarter = quarter;
                    _log.WriteLine($"{Stamp}         {label}: {f * 100:0}%");
                }
                if (!_interactive) return;
                if (sw.ElapsedMilliseconds - lastDraw < 100 && done < total) return;
                lastDraw = sw.ElapsedMilliseconds;
                const int W = 30;
                int fill = (int)(f * W);
                double eta = f > 0.01 ? sw.Elapsed.TotalSeconds * (1 - f) / f : 0;
                Console.Write($"\r      {label,-26} [{new string('#', fill)}{new string('.', W - fill)}] {f * 100,5:0.0}%  ETA {eta,4:0}s ");
                _barShown = true;
            }
        };
    }

    /// <summary>Progress callback for chip erase (fraction 0..1).</summary>
    public Action<double> FractionBar(string label)
    {
        var b = Bar(label, 1000);
        return f => b((long)(f * 1000), 0);
    }

    void ClearBar()
    {
        if (!_barShown) return;
        Console.Write("\r" + new string(' ', 90) + "\r");
        _barShown = false;
    }

    public void Finish()
    {
        Line($"\n{Stamp} {(_fail == 0 ? "ALL TESTS PASSED" : $"{_fail} FAILURES")} — {_pass} passed, {_fail} failed in {_total.Elapsed:mm\\:ss}");
    }

    public void Dispose() => _log.Dispose();
}
