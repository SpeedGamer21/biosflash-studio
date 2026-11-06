namespace BiosFlashStudio.Hardware;

/// <summary>
/// Virtual CH341A with an emulated Winbond-style SPI NOR flash. Lets every feature of the
/// application be exercised without hardware. Memory persists while the app runs.
/// </summary>
public sealed class SimulatorProgrammer : IProgrammer
{
    static SimulatorProgrammer _shared;
    public static SimulatorProgrammer Shared => _shared ??= new SimulatorProgrammer(0xEF4018, 16 * 1024 * 1024);

    readonly byte[] _mem;
    readonly byte[] _eeprom = new byte[64 * 1024];
    readonly uint _jedec;
    byte _sr1, _sr2 = 0x02, _sr3 = 0x60;
    bool _wel, _fourByte;
    long _busyUntil;
    readonly Random _rnd = new(1234);

    public string Name => "CH341A (virtual)";
    public string Backend => "Simulator";
    public string DriverInfo => "Built-in emulator";
    public string DeviceId => "SIM\\VID_1A86&PID_5512\\VIRTUAL";
    public bool IsOpen { get; private set; }
    public int MaxTransfer => 4096 + 8;
    public bool SupportsI2c => true;
    public long FlashSize => _mem.Length;

    public SimulatorProgrammer(uint jedec, int size)
    {
        _jedec = jedec;
        _mem = Analysis.DemoImage.Build(size);
        Array.Fill(_eeprom, (byte)0xFF);
        for (int i = 0; i < 256; i++) _eeprom[i] = (byte)i;
    }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    public void Dispose() => Close();
    public void SetSpeed(int speed) { }

    bool Busy => Environment.TickCount64 < _busyUntil;

    long Addr(byte[] w, int at, bool four)
    {
        long a = four ? ((long)w[at] << 24) | ((long)w[at + 1] << 16) | ((long)w[at + 2] << 8) | w[at + 3]
                      : (w[at] << 16) | (w[at + 1] << 8) | w[at + 2];
        return a % _mem.Length;
    }

    bool Protected => (_sr1 & 0x1C) != 0;

    public byte[] SpiTransfer(byte[] w, int readLen)
    {
        if (!IsOpen) throw new ProgrammerException("Simulator not open");
        if (w.Length + readLen > MaxTransfer) throw new ArgumentException("SPI transfer too large");
        var r = new byte[readLen];
        Array.Fill(r, (byte)0xFF);
        if (w.Length == 0) return r;
        byte op = w[0];
        if (Busy && op != 0x05) return r;
        int ab = _fourByte ? 4 : 3;
        switch (op)
        {
            case 0x9F:
                if (readLen > 0) r[0] = (byte)(_jedec >> 16);
                if (readLen > 1) r[1] = (byte)(_jedec >> 8);
                if (readLen > 2) r[2] = (byte)_jedec;
                break;
            case 0x05: for (int i = 0; i < readLen; i++) r[i] = (byte)(_sr1 | (_wel ? 2 : 0) | (Busy ? 1 : 0)); break;
            case 0x35: for (int i = 0; i < readLen; i++) r[i] = _sr2; break;
            case 0x15: for (int i = 0; i < readLen; i++) r[i] = _sr3; break;
            case 0x06: _wel = true; break;
            case 0x04: _wel = false; break;
            case 0x01:
                if (_wel) { if (w.Length > 1) _sr1 = (byte)(w[1] & 0xFC); if (w.Length > 2) _sr2 = w[2]; _busyUntil = Environment.TickCount64 + 5; }
                _wel = false; break;
            case 0x31: if (_wel && w.Length > 1) _sr2 = w[1]; _wel = false; break;
            case 0xB7: _fourByte = true; break;
            case 0xE9: _fourByte = false; break;
            case 0x03:
            case 0x13:
            case 0x0B:
            {
                bool four = op == 0x13 || _fourByte;
                long a = Addr(w, 1, four);
                for (int i = 0; i < readLen; i++) r[i] = _mem[(a + i) % _mem.Length];
                break;
            }
            case 0x02:
            case 0x12:
            {
                bool four = op == 0x12 || _fourByte;
                int hdr = 1 + (four ? 4 : 3);
                if (_wel && !Protected && w.Length > hdr)
                {
                    long a = Addr(w, 1, four);
                    long pageBase = a & ~0xFFL;
                    int off = (int)(a & 0xFF);
                    for (int i = hdr; i < w.Length; i++)
                    {
                        _mem[pageBase + off] &= w[i];
                        off = (off + 1) & 0xFF;
                    }
                }
                _wel = false;
                break;
            }
            case 0x20: case 0x21: EraseRange(w, op == 0x21, 4096); break;
            case 0x52: EraseRange(w, false, 32768); break;
            case 0xD8: case 0xDC: EraseRange(w, op == 0xDC, 65536); break;
            case 0xC7:
            case 0x60:
                if (_wel && !Protected) { Array.Fill(_mem, (byte)0xFF); _busyUntil = Environment.TickCount64 + 1500 + _mem.Length / 20000; }
                _wel = false; break;
            case 0x5A:
            {
                long a = (w[1] << 16) | (w[2] << 8) | w[3];
                var sfdp = BuildSfdp();
                for (int i = 0; i < readLen; i++) r[i] = a + i < sfdp.Length ? sfdp[a + i] : (byte)0xFF;
                break;
            }
            case 0x4B:
                for (int i = 0; i < Math.Min(8, readLen); i++) r[i] = (byte)(0xD2 + i * 17);
                break;
            case 0xAB: for (int i = 0; i < readLen; i++) r[i] = (byte)((_jedec & 0xFF) - 1); break;
            case 0x90: if (readLen > 0) r[0] = (byte)(_jedec >> 16); if (readLen > 1) r[1] = (byte)((_jedec & 0xFF) - 1); break;
        }
        _ = ab;
        // emulate USB latency so progress bars move realistically
        if (_rnd.Next(12) == 0) Thread.Sleep(1);
        return r;
    }

    void EraseRange(byte[] w, bool force4, int size)
    {
        if (_wel && !Protected)
        {
            long a = Addr(w, 1, force4 || _fourByte) & ~(long)(size - 1);
            Array.Fill(_mem, (byte)0xFF, (int)a, size);
            _busyUntil = Environment.TickCount64 + (size >= 65536 ? 20 : 3);
        }
        _wel = false;
    }

    byte[] BuildSfdp()
    {
        var s = new byte[0x80];
        Array.Fill(s, (byte)0xFF);
        "SFDP"u8.CopyTo(s);
        s[4] = 6; s[5] = 1; s[6] = 0; s[7] = 0xFF;
        s[8] = 0; s[9] = 6; s[10] = 1; s[11] = 16; s[12] = 0x30; s[13] = 0; s[14] = 0; s[15] = 0xFF;
        // BFPT dword1 / dword2 (density in bits - 1)
        uint bits = (uint)(_mem.Length * 8L - 1);
        BitConverter.GetBytes(0xFFF920E5u).CopyTo(s, 0x30);
        BitConverter.GetBytes(bits).CopyTo(s, 0x34);
        return s;
    }

    public void I2cRead(int eepromId, int addr, byte[] buffer, int offset, int len)
    {
        for (int i = 0; i < len; i++) buffer[offset + i] = _eeprom[(addr + i) % _eeprom.Length];
        Thread.Sleep(Math.Max(1, len / 512));
    }

    public void I2cWrite(int eepromId, int addr, byte[] buffer, int offset, int len)
    {
        for (int i = 0; i < len; i++) _eeprom[(addr + i) % _eeprom.Length] = buffer[offset + i];
        Thread.Sleep(Math.Max(1, len / 256));
    }
}

