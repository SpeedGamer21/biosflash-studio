using System.Diagnostics;
using BiosFlashStudio.Analysis;
using BiosFlashStudio.Flash;
using BiosFlashStudio.Hardware;

// Headless self-test for BiosFlash Studio. Everything runs in software:
// the analyzer, the SPI/I²C drivers against the simulator, the raw CH341 USB protocol
// against a packet-level emulator, and the ASUS capsule tool.
//
//   dotnet run --project tests/SelfTest -c Release            (or run-tests.bat / run-tests.sh)
//   dotnet run --project tests/SelfTest -c Release -- --quick  (skips the slow 32 MB chip)
//   dotnet run --project tests/SelfTest -c Release -- --repair (only the repair tools + update extractor, a few seconds)

bool quick = args.Contains("--quick");
Console.OutputEncoding = System.Text.Encoding.UTF8;
var logPath = Path.Combine(AppContext.BaseDirectory, "selftest.log");
using var rep = new Reporter(args.Contains("--repair") ? 3 : quick ? 9 : 10, logPath);
Console.WriteLine($"Watch progress from another terminal:  tail -f \"{logPath}\"");
if (args.Contains("--repair"))
{
    Tests3.Run(rep, Path.GetDirectoryName(logPath));
    rep.Finish();
    return rep.Failures == 0 ? 0 : 1;
}

// ---------------------------------------------------------------- analyzer
rep.Section("Firmware analyzer (synthetic 16 MB image)");
var img = DemoImage.Build(16 * 1024 * 1024);
var r = BiosAnalyzer.Analyze(img);
rep.Detail(r.Report);
rep.Check(r.HasDescriptor && r.IfdVersion == 2, "descriptor v2");
rep.Check(r.Regions.Count == 4, "4 regions: " + r.Regions.Count);
rep.Check(r.Me.Version == "14.0.39.1339", "ME version " + r.Me.Version);
rep.Check(r.Dmi1("MODEL") == "DemoBook 14 G2 (Simulated)", "model " + r.Dmi1("MODEL"));
rep.Check(r.Dmi1("SERIAL") == "SIM0000001", "serial");
rep.Check(r.OemKey == "DEMO1-SIMUL-ATED0-KEY00-NOTRL", "oem key");
rep.Check(r.GbeMac == "02:DE:5A:00:12:34", "mac");
rep.Check(r.Nvram.Count(v => v.Format == "VSS") == 5, "nvram vars " + r.Nvram.Count);
rep.Check(r.Partitions.Count == 3, "fpt partitions");
rep.Check(r.BiosVersion.StartsWith("DEMO.01.05"), "bios version " + r.BiosVersion);
var copy = (byte[])img.Clone();
rep.Check(BiosAnalyzer.UnlockDescriptor(copy) && BiosAnalyzer.Analyze(copy).DescriptorLock.StartsWith("UNLOCKED"), "unlock descriptor");
var rnd = new byte[8 * 1024 * 1024]; new Random(5).NextBytes(rnd);
var sw = Stopwatch.StartNew(); BiosAnalyzer.Analyze(rnd);
rep.Info($"random 8 MB analyze: {sw.ElapsedMilliseconds} ms");

// ---------------------------------------------------------------- flash engine on simulator
var chips = quick ? new[] { (0xEF4018u, 16 << 20) } : new[] { (0xEF4018u, 16 << 20), (0xEF4019u, 32 << 20) };
foreach (var (id, size) in chips)
{
    rep.Section($"SPI flash driver on simulator — {id:X6}, {size >> 20} MB");
    var sim = new SimulatorProgrammer(id, size); sim.Open();
    var chip = ChipDatabase.FindById(id)[0];
    var f = new SpiFlash(sim, chip) { Log = m => rep.Info(m) };
    rep.Check(f.ReadJedecId() == id, $"jedec {id:X6}");
    rep.Check(f.ReadSfdpSize() == size, "sfdp size");
    f.Begin();
    sw.Restart();
    var d = f.Read(0, size, default, rep.Bar("Read", size));
    rep.Info($"read {size >> 20} MB: {sw.ElapsedMilliseconds} ms");
    var expect = DemoImage.Build(size);
    rep.Check(d.AsSpan().SequenceEqual(expect), "read == content");
    f.WriteSr1(0x1C);
    f.EraseSector(0);
    rep.Check(f.Read(0, 16, default, null)[0] != 0xFF, "protected sector not erased");
    f.Unprotect();
    f.EraseChip(default, rep.FractionBar("Chip erase"));
    var ff = new byte[size]; Array.Fill(ff, (byte)0xFF);
    rep.Check(f.Verify(0, ff, 0, size, default, rep.Bar("Blank check", size)).Ok, "blank after erase");
    f.Program(0, expect, 0, size, true, default, rep.Bar("Program", size));
    rep.Check(f.Verify(0, expect, 0, size, default, rep.Bar("Verify", size)).Ok, "verify after program");
    var mod = (byte[])expect.Clone();
    mod[0x1234] ^= 0xFF; mod[size - 5] = 0x00; mod[size - 4] = 0xAB;
    var (sk, er, wr) = f.SmartProgram(0, mod, size, default, rep.Bar("Smart program", size));
    rep.Info($"smart: skipped {sk}, erased {er}, written {wr}");
    rep.Check(wr == 2 && f.Verify(0, mod, 0, size, default, rep.Bar("Verify (smart)", size)).Ok, "smart program");
    f.EraseRange(0x10000, 0x2000, true, default, null);
    rep.Check(f.Verify(0x10000, ff, 0x10000, 0x2000, default, null).Ok, "erase range");
    f.End();
}

// ---------------------------------------------------------------- I²C on simulator
rep.Section("I²C EEPROM driver on simulator (24C256)");
var s2 = new SimulatorProgrammer(0xEF4018, 16 << 20); s2.Open();
var ee = new I2cEeprom(s2, ChipDatabase.I2cChips.First(c => c.Name == "24C256"));
var buf = new byte[32768]; new Random(1).NextBytes(buf);
ee.Write(buf, default, rep.Bar("EEPROM write", buf.Length));
rep.Check(ee.Read(default, rep.Bar("EEPROM read", buf.Length)).AsSpan().SequenceEqual(buf), "i2c roundtrip");
rep.Info($"chip database: {ChipDatabase.SpiChips.Count()} SPI, {ChipDatabase.I2cChips.Count()} I²C profiles");

Tests2.Run(rep, Path.GetDirectoryName(logPath)!);
Tests3.Run(rep, Path.GetDirectoryName(logPath));

rep.Finish();
return rep.Failures == 0 ? 0 : 1;
