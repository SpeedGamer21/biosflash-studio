using BiosFlashStudio.Hardware;

/// <summary>Decodes raw CH341 bulk packets and emulates the attached SPI flash / I²C EEPROM.</summary>
sealed class FakeCh341 : Ch341DirectProgrammer
{
    readonly SimulatorProgrammer _flash;
    readonly byte[] _ee = new byte[128 * 1024];
    int _eeAddr, _addrBytesSeen;
    bool _devPhase;
    byte _dev;
    public int Violations;
    public int AddrBytes { get; set; } = 1;

    public FakeCh341(SimulatorProgrammer flash) { _flash = flash; Array.Fill(_ee, (byte)0xFF); }
    public override string Name => "fake";
    public override string Backend => "fake";
    public override string DriverInfo => "";
    public override string DeviceId => "";
    protected override void TransportOpen() { }
    protected override void TransportClose() { }
    protected override void WaitWriteCycle() { }   // emulator has no write cycle
    protected override void BulkWrite(byte[] d, int len) => Process(d, len, null);

    protected override byte[] Exchange(byte[] o, int inLen)
    {
        var resp = new List<byte>();
        Process(o, o.Length, resp);
        if (resp.Count != inLen) { Violations++; Console.WriteLine($"  response {resp.Count} != expected {inLen}"); }
        return resp.ToArray();
    }

    static byte Rev(byte b) { int r = 0; for (int k = 0; k < 8; k++) { r = (r << 1) | (b & 1); b >>= 1; } return (byte)r; }
    public bool EepromEquals(byte[] d) => _ee.AsSpan(0, d.Length).SequenceEqual(d);

    void Process(byte[] d, int len, List<byte> resp)
    {
        if (len > 32 && len % 32 != 0 && d[0] == 0xAB && len > 32 && d[32] == 0xA8)
        {
            // last SPI packet may be short; everything before must be full 32-byte packets
        }
        var mosi = new List<byte>();
        for (int p = 0; p < len; p += 32)
        {
            int n = Math.Min(32, len - p);
            byte cmd = d[p];
            if (cmd == 0xA8)
            {
                for (int i = 1; i < n; i++) mosi.Add(Rev(d[p + i]));
            }
            else if (cmd == 0xAB) { }
            else if (cmd == 0xAA) I2c(d, p + 1, p + n, resp);
            else { Violations++; Console.WriteLine($"  unknown packet cmd {cmd:X2} at {p}"); }
        }
        if (mosi.Count > 0)
        {
            int w = mosi.Count;
            while (w > 1 && mosi[w - 1] == 0xFF) w--;
            var r = _flash.SpiTransfer(mosi.Take(w).ToArray(), mosi.Count - w);
            for (int i = 0; i < w; i++) resp?.Add(Rev(0xFF));
            foreach (var b in r) resp?.Add(Rev(b));
        }
    }

    void I2c(byte[] d, int i, int end, List<byte> resp)
    {
        while (i < end)
        {
            byte c = d[i++];
            if (c == 0x00) return;
            if (c == 0x74) { _devPhase = true; continue; }
            if (c == 0x75) continue;
            if ((c & 0xE0) == 0x60) continue;
            if ((c & 0xC0) == 0x80)
            {
                int n = c & 0x3F;
                if (n == 0) { Violations++; n = 1; }
                for (int k = 0; k < n; k++)
                {
                    byte b = d[i++];
                    if (_devPhase)
                    {
                        _dev = b; _devPhase = false;
                        if ((b & 1) == 0) { _addrBytesSeen = 0; _eeAddr = 0; }
                        continue;
                    }
                    if ((_dev & 1) == 1) { Violations++; continue; }
                    if (_addrBytesSeen < AddrBytes)
                    {
                        _eeAddr = (_eeAddr << 8) | b;
                        if (++_addrBytesSeen == AddrBytes) _eeAddr |= AddrBytes == 2 ? (_dev & 0x02) << 15 : (_dev & 0x0E) << 7;
                        continue;
                    }
                    _ee[_eeAddr++ % _ee.Length] = b;
                }
                continue;
            }
            if ((c & 0xC0) == 0xC0)
            {
                int n = c & 0x3F;
                if (n == 0) n = 1;
                for (int k = 0; k < n; k++) resp?.Add(_ee[_eeAddr++ % _ee.Length]);
                continue;
            }
            Violations++;
            Console.WriteLine($"  bad i2c cmd {c:X2}");
        }
    }
}
