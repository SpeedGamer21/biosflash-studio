namespace BiosFlashStudio.Flash;

public enum ChipKind { SpiFlash, I2cEeprom }

[Flags]
public enum ChipFlags { None = 0, Aai = 1, GlobalUnlock = 2, Generic = 4 }

public sealed class ChipInfo
{
    public string Manufacturer { get; init; } = "";
    public string Name { get; init; } = "";
    public uint JedecId { get; init; }
    public long Size { get; init; }
    public int PageSize { get; init; } = 256;
    public string Voltage { get; init; } = "3.3V";
    public ChipKind Kind { get; init; } = ChipKind.SpiFlash;
    public ChipFlags Flags { get; init; }
    public int EepromId { get; init; } = -1;
    public string Notes { get; init; } = "";

    public bool Is18V => Voltage.StartsWith("1.8");
    public bool NeedsFourByte => Size > 16L * 1024 * 1024;
    public string JedecText => Kind == ChipKind.I2cEeprom ? "—" : $"{JedecId:X6}";
    public string SizeText => FormatSize(Size);
    public string Display => Kind == ChipKind.I2cEeprom ? $"{Name}  ({SizeText})" : $"{Manufacturer} {Name}  ({SizeText}, {Voltage})";
    public override string ToString() => Display;

    public static string FormatSize(long s)
        => s >= 1024 * 1024 ? $"{s / 1024.0 / 1024.0:0.##} MB" : s >= 1024 ? $"{s / 1024.0:0.##} KB" : $"{s} B";
}

public static class ChipDatabase
{
    const long K = 1024, M = 1024 * 1024;
    public static readonly List<ChipInfo> All = new();

    static void S(string mfr, string name, uint id, long size, string v = "3.3V", ChipFlags f = ChipFlags.None, string notes = "")
        => All.Add(new ChipInfo { Manufacturer = mfr, Name = name, JedecId = id, Size = size, Voltage = v, Flags = f, Notes = notes });

    static void E(string name, int id, long size, int page)
        => All.Add(new ChipInfo { Manufacturer = "Generic", Name = name, Size = size, PageSize = page, Kind = ChipKind.I2cEeprom, EepromId = id, Voltage = "1.8–5V", Notes = "I²C serial EEPROM" });

    static ChipDatabase()
    {
        const string V18 = "1.8V", VW = "1.65–3.6V";
        // ---- Winbond
        S("Winbond", "W25X10", 0xEF3011, 128 * K); S("Winbond", "W25X20", 0xEF3012, 256 * K);
        S("Winbond", "W25X40", 0xEF3013, 512 * K); S("Winbond", "W25X80", 0xEF3014, 1 * M);
        S("Winbond", "W25X16", 0xEF3015, 2 * M); S("Winbond", "W25X32", 0xEF3016, 4 * M); S("Winbond", "W25X64", 0xEF3017, 8 * M);
        S("Winbond", "W25Q40", 0xEF4013, 512 * K); S("Winbond", "W25Q80", 0xEF4014, 1 * M);
        S("Winbond", "W25Q16JV/BV", 0xEF4015, 2 * M); S("Winbond", "W25Q32JV/FV", 0xEF4016, 4 * M);
        S("Winbond", "W25Q64JV/FV", 0xEF4017, 8 * M); S("Winbond", "W25Q128JV/FV", 0xEF4018, 16 * M);
        S("Winbond", "W25Q256JV-IQ/FV", 0xEF4019, 32 * M, notes: "4-byte addressing");
        S("Winbond", "W25Q256JV-IQ-JQ", 0xEF4019, 32 * M, notes: "4-byte addressing, industrial");
        S("Winbond", "W25Q512JV", 0xEF4020, 64 * M, notes: "4-byte addressing");
        S("Winbond", "W25Q64JV-IM/JM", 0xEF7017, 8 * M); S("Winbond", "W25Q128JV-IM/JM", 0xEF7018, 16 * M);
        S("Winbond", "W25Q256JV-IM", 0xEF7019, 32 * M, notes: "4-byte addressing");
        S("Winbond", "W25Q16JW", 0xEF6015, 2 * M, V18); S("Winbond", "W25Q32JW/FW", 0xEF6016, 4 * M, V18);
        S("Winbond", "W25Q64FW/JW", 0xEF6017, 8 * M, V18); S("Winbond", "W25Q128FW/JW", 0xEF6018, 16 * M, V18);
        S("Winbond", "W25Q256JW", 0xEF6019, 32 * M, V18); S("Winbond", "W25Q128JW-IM", 0xEF8018, 16 * M, V18);
        S("Winbond", "W25Q256JW-IM", 0xEF8019, 32 * M, V18);
        // ---- Macronix
        S("Macronix", "MX25L512E", 0xC22010, 64 * K); S("Macronix", "MX25L1006E", 0xC22011, 128 * K);
        S("Macronix", "MX25L2006E", 0xC22012, 256 * K); S("Macronix", "MX25L4006E", 0xC22013, 512 * K);
        S("Macronix", "MX25L8006E/8035E", 0xC22014, 1 * M); S("Macronix", "MX25L1606E/1635E", 0xC22015, 2 * M);
        S("Macronix", "MX25L3206E/3233F", 0xC22016, 4 * M); S("Macronix", "MX25L6406E/6433F", 0xC22017, 8 * M);
        S("Macronix", "MX25L12835F/12873F", 0xC22018, 16 * M); S("Macronix", "MX25L25635F/25645G", 0xC22019, 32 * M, notes: "4-byte addressing");
        S("Macronix", "MX25L51245G", 0xC2201A, 64 * M, notes: "4-byte addressing");
        S("Macronix", "MX25U1635F", 0xC22535, 2 * M, V18); S("Macronix", "MX25U3235F", 0xC22536, 4 * M, V18);
        S("Macronix", "MX25U6435F", 0xC22537, 8 * M, V18); S("Macronix", "MX25U12835F", 0xC22538, 16 * M, V18);
        S("Macronix", "MX25U25635F", 0xC22539, 32 * M, V18); S("Macronix", "MX25U51245G", 0xC2253A, 64 * M, V18);
        S("Macronix", "MX25R3235F", 0xC22816, 4 * M, VW); S("Macronix", "MX25R6435F", 0xC22817, 8 * M, VW);
        // ---- GigaDevice
        S("GigaDevice", "GD25Q40", 0xC84013, 512 * K); S("GigaDevice", "GD25Q80", 0xC84014, 1 * M);
        S("GigaDevice", "GD25Q16", 0xC84015, 2 * M); S("GigaDevice", "GD25Q32", 0xC84016, 4 * M);
        S("GigaDevice", "GD25Q64/B64", 0xC84017, 8 * M); S("GigaDevice", "GD25Q128/B128", 0xC84018, 16 * M);
        S("GigaDevice", "GD25Q256/B256", 0xC84019, 32 * M, notes: "4-byte addressing");
        S("GigaDevice", "GD25LQ16", 0xC86015, 2 * M, V18); S("GigaDevice", "GD25LQ32", 0xC86016, 4 * M, V18);
        S("GigaDevice", "GD25LQ64", 0xC86017, 8 * M, V18); S("GigaDevice", "GD25LQ128", 0xC86018, 16 * M, V18);
        S("GigaDevice", "GD25LB256", 0xC86019, 32 * M, V18);
        // ---- EON / cFeon
        S("EON", "EN25F40", 0x1C3113, 512 * K); S("EON", "EN25F80", 0x1C3114, 1 * M);
        S("EON", "EN25F16", 0x1C3115, 2 * M); S("EON", "EN25F32", 0x1C3116, 4 * M);
        S("EON", "EN25Q80", 0x1C3014, 1 * M); S("EON", "EN25Q16", 0x1C3015, 2 * M);
        S("EON", "EN25Q32", 0x1C3016, 4 * M); S("EON", "EN25Q64", 0x1C3017, 8 * M); S("EON", "EN25Q128", 0x1C3018, 16 * M);
        S("EON", "EN25QH16", 0x1C7015, 2 * M); S("EON", "EN25QH32", 0x1C7016, 4 * M);
        S("EON", "EN25QH64", 0x1C7017, 8 * M); S("EON", "EN25QH128", 0x1C7018, 16 * M);
        S("EON", "EN25QH256", 0x1C7019, 32 * M, notes: "4-byte addressing");
        // ---- Spansion / Cypress / Infineon
        S("Spansion", "S25FL004A", 0x010212, 512 * K); S("Spansion", "S25FL008A", 0x010213, 1 * M);
        S("Spansion", "S25FL016A", 0x010214, 2 * M); S("Spansion", "S25FL032A/P", 0x010215, 4 * M);
        S("Spansion", "S25FL064A/P", 0x010216, 8 * M); S("Spansion", "S25FL128P/S", 0x012018, 16 * M);
        S("Spansion", "S25FL256S", 0x010219, 32 * M, notes: "4-byte addressing");
        S("Spansion", "S25FL116K", 0x014015, 2 * M); S("Spansion", "S25FL132K", 0x014016, 4 * M); S("Spansion", "S25FL164K", 0x014017, 8 * M);
        // ---- Micron / Numonyx / ST
        S("ST/Micron", "M25P05", 0x202010, 64 * K); S("ST/Micron", "M25P10", 0x202011, 128 * K);
        S("ST/Micron", "M25P20", 0x202012, 256 * K); S("ST/Micron", "M25P40", 0x202013, 512 * K);
        S("ST/Micron", "M25P80", 0x202014, 1 * M); S("ST/Micron", "M25P16", 0x202015, 2 * M);
        S("ST/Micron", "M25P32", 0x202016, 4 * M); S("ST/Micron", "M25P64", 0x202017, 8 * M); S("ST/Micron", "M25P128", 0x202018, 16 * M);
        S("ST/Micron", "M25PX16", 0x207115, 2 * M); S("ST/Micron", "M25PX32", 0x207116, 4 * M); S("ST/Micron", "M25PX64", 0x207117, 8 * M);
        S("Micron", "N25Q032A", 0x20BA16, 4 * M); S("Micron", "N25Q064A", 0x20BA17, 8 * M);
        S("Micron", "N25Q128A/MT25QL128", 0x20BA18, 16 * M); S("Micron", "N25Q256A/MT25QL256", 0x20BA19, 32 * M, notes: "4-byte addressing");
        S("Micron", "MT25QL512", 0x20BA20, 64 * M, notes: "4-byte addressing");
        S("Micron", "N25Q064A 1.8V", 0x20BB17, 8 * M, V18); S("Micron", "N25Q128A/MT25QU128", 0x20BB18, 16 * M, V18);
        S("Micron", "MT25QU256", 0x20BB19, 32 * M, V18);
        // ---- XMC
        S("XMC", "XM25QH64C", 0x204017, 8 * M); S("XMC", "XM25QH128C", 0x204018, 16 * M); S("XMC", "XM25QH256C", 0x204019, 32 * M);
        S("XMC", "XM25QH64A", 0x207017, 8 * M); S("XMC", "XM25QH128A", 0x207018, 16 * M);
        S("XMC", "XM25QU64C", 0x204117, 8 * M, V18); S("XMC", "XM25QU128C", 0x204118, 16 * M, V18);
        // ---- ISSI
        S("ISSI", "IS25LP016", 0x9D6015, 2 * M); S("ISSI", "IS25LP032", 0x9D6016, 4 * M);
        S("ISSI", "IS25LP064", 0x9D6017, 8 * M); S("ISSI", "IS25LP128", 0x9D6018, 16 * M);
        S("ISSI", "IS25LP256", 0x9D6019, 32 * M); S("ISSI", "IS25WP032", 0x9D7016, 4 * M, V18);
        S("ISSI", "IS25WP064", 0x9D7017, 8 * M, V18); S("ISSI", "IS25WP128", 0x9D7018, 16 * M, V18); S("ISSI", "IS25WP256", 0x9D7019, 32 * M, V18);
        // ---- Atmel / Adesto / Renesas
        S("Atmel", "AT25DF041A", 0x1F4401, 512 * K); S("Atmel", "AT26DF081A", 0x1F4501, 1 * M);
        S("Atmel", "AT25DF081", 0x1F4502, 1 * M); S("Atmel", "AT25DF161", 0x1F4602, 2 * M);
        S("Atmel", "AT25DF321", 0x1F4700, 4 * M); S("Atmel", "AT25DF641", 0x1F4800, 8 * M);
        S("Adesto", "AT25SF041", 0x1F8401, 512 * K); S("Adesto", "AT25SF081", 0x1F8501, 1 * M);
        S("Adesto", "AT25SF161", 0x1F8601, 2 * M); S("Adesto", "AT25SF321", 0x1F8701, 4 * M);
        // ---- SST / Microchip
        S("SST", "SST25VF040B", 0xBF258D, 512 * K, f: ChipFlags.Aai, notes: "AAI word program");
        S("SST", "SST25VF080B", 0xBF258E, 1 * M, f: ChipFlags.Aai, notes: "AAI word program");
        S("SST", "SST25VF016B", 0xBF2541, 2 * M, f: ChipFlags.Aai, notes: "AAI word program");
        S("SST", "SST25VF032B", 0xBF254A, 4 * M, f: ChipFlags.Aai, notes: "AAI word program");
        S("SST", "SST25VF064C", 0xBF254B, 8 * M);
        S("Microchip", "SST26VF016B", 0xBF2641, 2 * M, f: ChipFlags.GlobalUnlock, notes: "Needs ULBPR (0x98) unlock");
        S("Microchip", "SST26VF032B", 0xBF2642, 4 * M, f: ChipFlags.GlobalUnlock, notes: "Needs ULBPR (0x98) unlock");
        S("Microchip", "SST26VF064B", 0xBF2643, 8 * M, f: ChipFlags.GlobalUnlock, notes: "Needs ULBPR (0x98) unlock");
        // ---- Others common on laptops / routers / TVs
        S("Fudan", "FM25Q16", 0xA14015, 2 * M); S("Fudan", "FM25Q32", 0xA14016, 4 * M);
        S("Fudan", "FM25Q64", 0xA14017, 8 * M); S("Fudan", "FM25Q128", 0xA14018, 16 * M);
        S("Zbit", "ZB25VQ16", 0x5E4015, 2 * M); S("Zbit", "ZB25VQ32", 0x5E4016, 4 * M);
        S("Zbit", "ZB25VQ64", 0x5E4017, 8 * M); S("Zbit", "ZB25VQ128", 0x5E4018, 16 * M);
        S("BoyaMicro", "BY25Q16", 0x684015, 2 * M); S("BoyaMicro", "BY25Q32", 0x684016, 4 * M);
        S("BoyaMicro", "BY25Q64", 0x684017, 8 * M); S("BoyaMicro", "BY25Q128", 0x684018, 16 * M);
        S("Puya", "P25Q16H", 0x856015, 2 * M, VW); S("Puya", "P25Q32H", 0x856016, 4 * M, VW);
        S("Puya", "P25Q64H", 0x856017, 8 * M, VW); S("Puya", "P25Q128H", 0x856018, 16 * M, VW);
        S("XTX", "XT25F16B", 0x0B4015, 2 * M); S("XTX", "XT25F32B", 0x0B4016, 4 * M);
        S("XTX", "XT25F64B", 0x0B4017, 8 * M); S("XTX", "XT25F128B", 0x0B4018, 16 * M);
        S("ESMT", "F25L016A", 0x8C2015, 2 * M); S("ESMT", "F25L032QA", 0x8C4116, 4 * M); S("ESMT", "F25L064QA", 0x8C4117, 8 * M);
        S("AMIC", "A25L016", 0x373015, 2 * M); S("AMIC", "A25L032", 0x373016, 4 * M); S("AMIC", "A25LQ64", 0x374017, 8 * M);
        S("PMC", "Pm25LQ032", 0x7F9D46, 4 * M); S("PMC", "Pm25LQ064", 0x7F9D47, 8 * M);

        // ---- I2C EEPROM (CH341DLL EEPROM_TYPE ids)
        E("24C01", 0, 128, 8); E("24C02", 1, 256, 8); E("24C04", 2, 512, 16); E("24C08", 3, 1 * K, 16);
        E("24C16", 4, 2 * K, 16); E("24C32", 5, 4 * K, 32); E("24C64", 6, 8 * K, 32); E("24C128", 7, 16 * K, 64);
        E("24C256", 8, 32 * K, 64); E("24C512", 9, 64 * K, 128); E("24C1024", 10, 128 * K, 256);
    }

    public static IEnumerable<ChipInfo> SpiChips => All.Where(c => c.Kind == ChipKind.SpiFlash);
    public static IEnumerable<ChipInfo> I2cChips => All.Where(c => c.Kind == ChipKind.I2cEeprom);

    public static List<ChipInfo> FindById(uint id) => SpiChips.Where(c => c.JedecId == id).ToList();

    public static string VendorName(byte mfr) => mfr switch
    {
        0xEF => "Winbond", 0xC2 => "Macronix", 0xC8 => "GigaDevice", 0x1C => "EON", 0x01 => "Spansion/Cypress",
        0x20 => "Micron/XMC", 0x9D => "ISSI", 0x1F => "Atmel/Adesto", 0xBF => "SST/Microchip", 0xA1 => "Fudan",
        0x5E => "Zbit", 0x68 => "BoyaMicro", 0x85 => "Puya", 0x0B => "XTX", 0x8C => "ESMT", 0x37 => "AMIC",
        0x7F => "PMC", 0x62 => "ON Semi", 0x89 => "Intel", 0xC4 => "GigaDevice", 0x25 => "Zetta", _ => $"Unknown (0x{mfr:X2})",
    };

    /// <summary>Builds a generic profile for a chip that is not in the database.</summary>
    public static ChipInfo Generic(uint id, long sfdpSize)
    {
        byte cap = (byte)(id & 0xFF);
        long size = sfdpSize > 0 ? sfdpSize : (cap >= 0x10 && cap <= 0x1A ? 1L << cap : cap == 0x20 ? 64 * M : cap == 0x21 ? 128 * M : 0);
        return new ChipInfo
        {
            Manufacturer = VendorName((byte)(id >> 16)),
            Name = $"Generic SPI ({id:X6})",
            JedecId = id,
            Size = size,
            Flags = ChipFlags.Generic,
            Notes = sfdpSize > 0 ? "Size from SFDP" : "Size guessed from JEDEC capacity byte",
        };
    }
}
