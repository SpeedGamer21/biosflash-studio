using System.IO.Compression;
using System.Text;
using BiosFlashStudio.Analysis;

/// <summary>Repair tools (NVRAM, ME clean, GbE MAC, region merge, identity transfer) and the update extractor.</summary>
static class Tests3
{
    public static void Run(Reporter rep, string outDir = null)
    {
        var demo = DemoImage.Build(16 << 20);
        var a = BiosAnalyzer.Analyze(demo);
        long biosStart = 0x500000;

        // ------------------------------------------------------------ NVRAM
        rep.Section("Repair: NVRAM, ME clean, GbE MAC");
        var stores = FirmwareRepair.FindNvramStores(demo);
        rep.Check(stores.Count == 1 && stores[0].Kind == "VSS" && stores[0].Start == biosStart + 0x20000 && stores[0].Variables == 5,
            "nvram: VSS store found — " + string.Join("; ", stores));
        var img = (byte[])demo.Clone();
        FirmwareRepair.ClearNvram(img, stores);
        var a2 = BiosAnalyzer.Analyze(img);
        rep.Check(a2.Nvram.Count == 0 && FirmwareRepair.FindNvramStores(img).Count == 1, "nvram: cleared, store header kept");

        // AMI NVAR chain
        var nv = (byte[])demo.Clone();
        long q = biosStart + 0x60000;
        foreach (var (name, data) in new[] { ("Setup", new byte[40]), ("Lang", Encoding.ASCII.GetBytes("eng")), ("Timeout", new byte[] { 5, 0 }) })
        {
            var nb = Encoding.ASCII.GetBytes(name + "\0");
            int size = 10 + 1 + nb.Length + data.Length;
            "NVAR"u8.ToArray().CopyTo(nv, q);
            BitConverter.GetBytes((ushort)size).CopyTo(nv, q + 4);
            nv[q + 6] = 0xFF; nv[q + 7] = 0xFF; nv[q + 8] = 0xFF;
            nv[q + 9] = 0x83;              // valid | ascii name | runtime
            nv[q + 10] = 0;                // GUID index
            nb.CopyTo(nv, q + 11);
            data.CopyTo(nv, q + 11 + nb.Length);
            q += size;
        }
        var ns = FirmwareRepair.FindNvramStores(nv).FirstOrDefault(s => s.Kind == "AMI NVAR");
        rep.Check(ns != null && ns.Variables == 3 && ns.End == q, "nvram: AMI NVAR store detected");
        FirmwareRepair.ClearNvram(nv, new[] { ns });
        rep.Check(!BiosAnalyzer.Analyze(nv).Nvram.Any(v => v.Format == "AMI NVAR"), "nvram: NVAR store cleared");

        // ME clean: demo is ME 14 (CSME) -> refused; patch to ME 10 -> allowed
        var plan14 = FirmwareRepair.PlanMeClean(demo, a);
        rep.Check(!plan14.Allowed && plan14.Reason.Contains("CSME"), "me: CSME 14 clean refused");
        var me10 = (byte[])demo.Clone();
        BitConverter.GetBytes((ushort)10).CopyTo(me10, 0x3000 + 0x1000 + 0x200 + 0x24);
        var a10 = BiosAnalyzer.Analyze(me10);
        var plan10 = FirmwareRepair.PlanMeClean(me10, a10);
        rep.Check(plan10.Allowed && plan10.Partitions.Any(p => p.name == "MFS" && p.abs == 0x3000 + 0x81000), $"me: ME {a10.Me.Version} clean plan — {plan10.Reason}");
        FirmwareRepair.ApplyMeClean(me10, plan10);
        var aMe = BiosAnalyzer.Analyze(me10);
        rep.Check(aMe.Partitions.First(p => p.Name == "MFS").State == "empty" && aMe.Me.Version == a10.Me.Version, "me: MFS wiped, FTPR intact");

        // GbE MAC
        var gbe = a.Regions.First(r => r.Index == 3);
        var banks = FirmwareRepair.GbeBanks(demo, gbe);
        rep.Check(banks.Count == 2 && banks.All(b => FirmwareRepair.GbeChecksumOk(demo, b)), "gbe: 2 valid NVM banks with good checksum");
        rep.Check(FirmwareRepair.ReadMac(demo, gbe) == "02:DE:5A:00:12:34", "gbe: read MAC");
        var mac = FirmwareRepair.ParseMac("02-11-22-33-44-55");
        var gimg = (byte[])demo.Clone();
        rep.Check(FirmwareRepair.SetMac(gimg, gbe, mac) == 2 && FirmwareRepair.GbeBanks(gimg, gbe).All(b => FirmwareRepair.GbeChecksumOk(gimg, b))
                  && BiosAnalyzer.Analyze(gimg).GbeMac == "02:11:22:33:44:55", "gbe: set MAC + checksum fixed");
        rep.Check(FirmwareRepair.ParseMac("01:00:5E:00:00:01") == null && FirmwareRepair.ParseMac("FF:FF:FF:FF:FF:FF") == null && FirmwareRepair.ParseMac("12:34") == null,
            "gbe: multicast / broadcast / short MAC rejected");

        // ------------------------------------------------------------ merge + identity
        rep.Section("Repair: region merge and identity transfer");
        // FD repair: dump has a wiped descriptor; regions must come from the stock image
        var dump = (byte[])demo.Clone();
        Array.Fill(dump, (byte)0xFF, 0, 0x1000);
        var plan = FirmwareRepair.PlanMerge(demo, a, dump, BiosAnalyzer.Analyze(dump));
        foreach (var r in plan) r.KeepFromDump = !r.Name.Contains("Descriptor");
        var fixedImg = FirmwareRepair.Merge(demo, dump, plan, out _);
        rep.Check(plan.Count == 4 && fixedImg.AsSpan(0, 0x1000).SequenceEqual(demo.AsSpan(0, 0x1000)) && BiosAnalyzer.Analyze(fixedImg).HasDescriptor,
            "merge: broken descriptor repaired from stock");

        // identity: dump has its own MAC + serial; stock is the factory image
        var own = (byte[])demo.Clone();
        FirmwareRepair.SetMac(own, gbe, FirmwareRepair.ParseMac("02:AB:CD:00:00:01"));
        long ser = BiosAnalyzer.IndexOf(own, "SIM0000001"u8, 0, own.Length);
        "SIM7777777"u8.ToArray().CopyTo(own, ser);
        var ownA = BiosAnalyzer.Analyze(own);
        var stock = (byte[])demo.Clone();
        long stockSer = BiosAnalyzer.IndexOf(stock, "SIM0000001"u8, 0, stock.Length);
        "DEFAULT000"u8.ToArray().CopyTo(stock, stockSer);
        var stockA = BiosAnalyzer.Analyze(stock);
        var ids = FirmwareRepair.PlanIdentity(own, ownA, stock, stockA);
        foreach (var i in ids) rep.Info($"identity: {i.Kind,-24} {i.OffsetText,-26} transfer={i.Transfer} · {i.Status}");
        rep.Check(ids.Any(i => i.Kind.StartsWith("GbE") && i.Transfer) && ids.Any(i => i.Kind.StartsWith("SMBIOS") && i.Transfer), "identity: MAC + DMI offered");
        var result = (byte[])stock.Clone();
        var done = FirmwareRepair.ApplyIdentity(result, own, stockA, ids);
        var ra = BiosAnalyzer.Analyze(result);
        rep.Check(ra.GbeMac == "02:AB:CD:00:00:01" && ra.Dmi1("SERIAL") == "SIM7777777", $"identity: applied ({string.Join(", ", done)}) → MAC {ra.GbeMac}, serial {ra.Dmi1("SERIAL")}");
        rep.Check(ra.Dmi1("UUID") == ownA.Dmi1("UUID") && ra.OemKey == ownA.OemKey, "identity: UUID and OEM key preserved");

        // ------------------------------------------------------------ extractor
        rep.Section("Update extractor (InsydeFlash, ZIP, capsule, UEFI volumes)");
        var ec = new byte[0x20000]; new Random(3).NextBytes(ec);
        var exe = new List<byte>();
        exe.AddRange("MZ"u8.ToArray()); exe.AddRange(new byte[0x3FE]);
        void Block(string tag, byte[] data)
        {
            exe.AddRange(Encoding.ASCII.GetBytes("$_IFLASH" + tag));
            exe.AddRange(BitConverter.GetBytes((uint)data.Length));
            exe.AddRange(BitConverter.GetBytes((uint)data.Length));
            exe.AddRange(data);
            exe.AddRange(new byte[0x123]);
        }
        Block("_INI_IMG", Encoding.ASCII.GetBytes("[Platform]\r\nModel=Demo\r\n"));
        Block("_BIOSIMG", demo);
        Block("_EC_IMG_", ec);
        if (outDir != null) File.WriteAllBytes(Path.Combine(outDir, "DemoInsydeUpdate.exe"), exe.ToArray());
        var ex = UpdateExtractor.Scan(exe.ToArray(), "DemoUpdate.exe");
        foreach (var c in ex.Candidates) rep.Info($"found: {c.Kind,-34} @{c.OffsetText} {c.SizeText,-8} score {c.Score}");
        rep.Check(ex.Candidates.Count > 0 && ex.Candidates[0].Data.AsSpan().SequenceEqual(demo), "extract: Insyde update → full image is best candidate");
        rep.Check(ex.Candidates.Any(c => c.Kind.Contains("EC") && c.Data.AsSpan().SequenceEqual(ec)), "extract: EC firmware block");
        rep.Check(ex.Candidates.Any(c => c.Kind.Contains("platform.ini")), "extract: platform.ini block");

        // ZIP containing an ASUS capsule
        var cap = new byte[0x800 + demo.Length];
        new Guid("14EEBB90-890A-43DB-AED1-5D3C4588A418").ToByteArray().CopyTo(cap, 0);
        BitConverter.GetBytes(0x800).CopyTo(cap, 16);
        BitConverter.GetBytes(cap.Length).CopyTo(cap, 24);
        demo.CopyTo(cap, 0x800);
        var zms = new MemoryStream();
        using (var z = new ZipArchive(zms, ZipArchiveMode.Create, true))
        {
            using var s = z.CreateEntry("BIOS/TUF-DEMO-ASUS-0301.CAP", CompressionLevel.Fastest).Open();
            s.Write(cap);
        }
        var zx = UpdateExtractor.Scan(zms.ToArray(), "download.zip");
        rep.Check(zx.Candidates.Any(c => c.Data.AsSpan().SequenceEqual(demo) && c.Source.Contains("TUF-DEMO")), "extract: capsule inside ZIP");

        // two UEFI firmware volumes back to back -> BIOS region candidate
        var fvFile = new byte[0x100123];
        new Random(4).NextBytes(fvFile);
        void Fv(int o, int len)
        {
            Array.Clear(fvFile, o, 0x48);
            new Guid("8C8CE578-8A3D-4F1C-9935-896185C32DD3").ToByteArray().CopyTo(fvFile, o + 0x10);
            BitConverter.GetBytes((ulong)len).CopyTo(fvFile, o + 0x20);
            "_FVH"u8.ToArray().CopyTo(fvFile, o + 0x28);
            BitConverter.GetBytes(0x0004FEFFu).CopyTo(fvFile, o + 0x2C);
            BitConverter.GetBytes((ushort)0x48).CopyTo(fvFile, o + 0x30);
            fvFile[o + 0x37] = 2;
            BitConverter.GetBytes((uint)(len / 0x1000)).CopyTo(fvFile, o + 0x38);
            BitConverter.GetBytes(0x1000u).CopyTo(fvFile, o + 0x3C);
            int sum = 0;
            for (int i = 0; i < 0x48; i += 2) sum += BitConverter.ToUInt16(fvFile, o + i);
            BitConverter.GetBytes((ushort)((0x10000 - (sum & 0xFFFF)) & 0xFFFF)).CopyTo(fvFile, o + 0x32);
        }
        Fv(0x1000, 0x40000);
        Fv(0x41000, 0x40000);
        var fx = UpdateExtractor.Scan(fvFile, "bios_region.upd");
        rep.Check(fx.Candidates.Any(c => c.Kind.StartsWith("UEFI BIOS region") && c.Offset == 0x1000 && c.Length == 0x80000), "extract: run of 2 firmware volumes");

        // unsupported container -> helpful note
        var sevenZ = new byte[0x20000];
        "MZ"u8.ToArray().CopyTo(sevenZ, 0);
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }.CopyTo(sevenZ, 0x8000);
        var sx = UpdateExtractor.Scan(sevenZ, "sp12345.exe");
        rep.Check(sx.Candidates.Count == 0 && sx.Notes.Any(n => n.Contains("7-Zip")), "extract: 7-Zip SFX explained");

        var small = FirmwareRepair.Resize(ec, 0x40000);
        rep.Check(small.Length == 0x40000 && small.AsSpan(0, ec.Length).SequenceEqual(ec) && small[^1] == 0xFF, "binary: pad EC image to 256 KB");
    }
}
