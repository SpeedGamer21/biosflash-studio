using BiosFlashStudio.Analysis;
using BiosFlashStudio.Flash;
using BiosFlashStudio.Hardware;

static class Tests2
{
    public static void Run(Reporter rep, string outDir)
    {
        rep.Section("Raw CH341 USB protocol (WinUSB + libusb code path) — SPI via packet emulator");
        var flashSim = new SimulatorProgrammer(0xEF4018, 16 << 20); flashSim.Open();
        var fake = new FakeCh341(flashSim); fake.Open();
        var chip = ChipDatabase.FindById(0xEF4018)[0];
        var f = new SpiFlash(fake, chip);
        rep.Check(f.ReadJedecId() == 0xEF4018, "proto: jedec via raw packets");
        f.Begin();
        var part = f.Read(0, 256 * 1024, default, rep.Bar("Read 256 KB", 256 * 1024));
        rep.Check(part.AsSpan().SequenceEqual(DemoImage.Build(16 << 20).AsSpan(0, 256 * 1024)), "proto: read 256 KB");
        f.Unprotect();
        f.EraseRange(0x200000, 0x10000, true, default, null);
        var pat = new byte[0x10000]; new Random(9).NextBytes(pat);
        f.Program(0x200000, pat, 0, pat.Length, false, default, null);
        rep.Check(f.Verify(0x200000, pat, 0, pat.Length, default, null).Ok, "proto: program + verify 64 KB");
        f.End();
        rep.Check(fake.Violations == 0, "proto: packet framing valid (" + fake.Violations + " violations)");
        rep.Section("Raw CH341 USB protocol — I²C EEPROMs via packet emulator");
        foreach (var name in new[] { "24C02", "24C16", "24C64", "24C512", "24C1024" })
        {
            var ec = ChipDatabase.I2cChips.First(c => c.Name == name);
            var fk = new FakeCh341(null) { AddrBytes = ec.EepromId >= 5 ? 2 : 1 }; fk.Open();
            var data = new byte[ec.Size]; new Random(ec.EepromId).NextBytes(data);
            var e2 = new I2cEeprom(fk, ec);
            e2.Write(data, default, rep.Bar($"{name} write", data.Length));
            var back = e2.Read(default, rep.Bar($"{name} read", data.Length));
            rep.Check(fk.EepromEquals(data) && back.AsSpan().SequenceEqual(data) && fk.Violations == 0, $"proto: I2C {name} (id {ec.EepromId}) roundtrip");
        }

        rep.Section("ASUS / AMI capsule tool");
        var body = DemoImage.Build(16 << 20);
        var cap = new byte[0x800 + body.Length];
        new Guid("14EEBB90-890A-43DB-AED1-5D3C4588A418").ToByteArray().CopyTo(cap, 0);
        BitConverter.GetBytes(0x800).CopyTo(cap, 16);
        BitConverter.GetBytes(0x50000).CopyTo(cap, 20);
        BitConverter.GetBytes(cap.Length).CopyTo(cap, 24);
        BitConverter.GetBytes((ushort)0x800).CopyTo(cap, 28);
        body.CopyTo(cap, 0x800);
        File.WriteAllBytes(Path.Combine(outDir, "PRIME-Z790-DEMO-ASUS-1402.CAP"), cap);
        var ci = CapsuleTool.Parse(cap, "PRIME-Z790-DEMO-ASUS-1402.CAP");
        rep.Check(ci.IsCapsule && ci.Kind.StartsWith("AMI Aptio") && ci.HeaderSize == 0x800, "cap: header " + ci.Kind);
        rep.Check(ci.BodyLength == body.Length && ci.Body.AsSpan().SequenceEqual(body), "cap: body extracted");
        rep.Check(ci.IsAsus && ci.BoardModel == "PRIME Z790 DEMO" && ci.BiosVersion == "1402", $"cap: asus name '{ci.BoardModel}' {ci.BiosVersion}");
        rep.Check(ci.Analysis?.HasDescriptor == true, "cap: analysis of body");
        var dump = (byte[])body.Clone();
        var gbe = ci.Analysis.Regions.First(g => g.Name.Contains("GbE", StringComparison.OrdinalIgnoreCase));
        dump[gbe.Start] ^= 0x55; dump[gbe.Start + 1] ^= 0x55;
        var plan = CapsuleTool.PlanMerge(ci, dump, BiosAnalyzer.Analyze(dump));
        foreach (var p in plan) rep.Info($"  plan: {p.Name,-12} {p.RangeText} keep={p.KeepFromDump} {p.Compare}");
        rep.Check(plan.Any(p => p.Name == gbe.Name && p.KeepFromDump && p.Compare == "differs"), "cap: plan keeps GbE");
        var merged = CapsuleTool.Merge(ci.Body, dump, plan, out int nCopied);
        rep.Check(merged.AsSpan().SequenceEqual(dump) && nCopied >= 1, "cap: merged image = capsule + dump GbE");
        var raw = CapsuleTool.Parse(body, "x.bin");
        rep.Check(!raw.IsCapsule && raw.BodyLength == body.Length, "cap: raw image passthrough");
        var trunc = CapsuleTool.Parse(cap[..(cap.Length - 4096)], "t.cap");
        rep.Check(trunc.Warnings.Any(w => w.Contains("truncated") || w.Contains("power of two")), "cap: truncated file warned");
        // signed capsule with trailer
        var signed = new byte[cap.Length + 0x1000];
        cap.CopyTo(signed, 0);
        new Guid("4A3CA68B-7723-48FB-803D-578CC1FEC44D").ToByteArray().CopyTo(signed, 0);
        BitConverter.GetBytes(signed.Length).CopyTo(signed, 24);
        var cs = CapsuleTool.Parse(signed, "s.cap");
        rep.Check(cs.IsCapsule && cs.BodyLength == body.Length && cs.TrailerLength == 0x1000, $"cap: signed trailer stripped ({cs.BodyLength:X}/{cs.TrailerLength:X})");
    }
}
