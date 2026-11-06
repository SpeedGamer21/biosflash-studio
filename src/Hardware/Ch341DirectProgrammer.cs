namespace BiosFlashStudio.Hardware;

/// <summary>
/// CH341A raw USB protocol (bulk endpoints 0x02 / 0x82, 32-byte packets), shared by the
/// WinUSB (Windows) and libusb (Linux) transports. SPI framing follows the publicly documented
/// stream commands used by flashrom; I²C uses the CH341 I²C stream command set.
/// </summary>
public abstract class Ch341DirectProgrammer : IProgrammer
{
    protected const byte EP_OUT = 0x02, EP_IN = 0x82;
    protected const int PKT = 32;
    const byte CMD_SPI_STREAM = 0xA8, CMD_I2C_STREAM = 0xAA, CMD_UIO_STREAM = 0xAB;
    const byte I2C_STM_STA = 0x74, I2C_STM_STO = 0x75, I2C_STM_OUT = 0x80, I2C_STM_IN = 0xC0, I2C_STM_SET = 0x60, I2C_STM_END = 0x00;
    const byte UIO_STM_OUT = 0x80, UIO_STM_DIR = 0x40, UIO_STM_END = 0x20, UIO_STM_US = 0xC0;

    int _speed = 1;
    bool _i2cNoted;

    public abstract string Name { get; }
    public abstract string Backend { get; }
    public abstract string DriverInfo { get; }
    public abstract string DeviceId { get; }
    public bool IsOpen { get; private set; }
    public int MaxTransfer => 4096 + 8;
    public bool SupportsI2c => true;
    public Action<string> Log { get; set; }

    /// <summary>Opens the USB transport (claim interface etc.).</summary>
    protected abstract void TransportOpen();
    protected abstract void TransportClose();
    /// <summary>Writes a buffer to EP 0x02 (whole buffer, split into 32-byte packets by the host controller).</summary>
    protected abstract void BulkWrite(byte[] data, int len);
    /// <summary>Writes <paramref name="outBuf"/> while concurrently reading exactly <paramref name="inLen"/> bytes from EP 0x82.</summary>
    protected abstract byte[] Exchange(byte[] outBuf, int inLen);

    public void Open()
    {
        if (IsOpen) return;
        TransportOpen();
        IsOpen = true;
        try
        {
            Configure();
            // Enable output pins: CS(D0)..D5 as outputs, CS high
            BulkWrite(new byte[] { CMD_UIO_STREAM, UIO_STM_OUT | 0x37, UIO_STM_DIR | 0x3F, UIO_STM_END }, 4);
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void Close()
    {
        if (!IsOpen) return;
        try { BulkWrite(new byte[] { CMD_UIO_STREAM, UIO_STM_OUT | 0x37, UIO_STM_DIR | 0x00, UIO_STM_END }, 4); } catch { }
        try { TransportClose(); } catch { }
        IsOpen = false;
    }

    public void Dispose() => Close();

    void Configure() => BulkWrite(new byte[] { CMD_I2C_STREAM, (byte)(I2C_STM_SET | (_speed & 3)), I2C_STM_END }, 3);

    public void SetSpeed(int speed)
    {
        _speed = Math.Clamp(speed, 0, 3);
        if (IsOpen) Configure();
    }

    // ------------------------------------------------------------------ SPI
    public byte[] SpiTransfer(byte[] write, int readLen)
    {
        if (!IsOpen) throw new ProgrammerException("Programmer is not open.");
        int total = write.Length + readLen;
        int packets = (total + PKT - 2) / (PKT - 1);
        var outBuf = new byte[PKT + packets + total];
        // Packet 0: CS high -> short delay -> CS low (padded to a full packet)
        outBuf[0] = CMD_UIO_STREAM;
        outBuf[1] = UIO_STM_OUT | 0x37;
        outBuf[2] = UIO_STM_US | 0x08;
        outBuf[3] = UIO_STM_OUT | 0x36;
        outBuf[4] = UIO_STM_END;
        int pos = PKT, idx = 0;
        var rev = BitRev.Table;
        while (idx < total)
        {
            int n = Math.Min(PKT - 1, total - idx);
            outBuf[pos++] = CMD_SPI_STREAM;
            for (int j = 0; j < n; j++, idx++)
                outBuf[pos++] = rev[idx < write.Length ? write[idx] : (byte)0xFF];
        }

        var raw = Exchange(outBuf, total);
        // Release CS
        BulkWrite(new byte[] { CMD_UIO_STREAM, UIO_STM_OUT | 0x37, UIO_STM_END }, 3);

        var result = new byte[readLen];
        for (int i = 0; i < readLen; i++) result[i] = rev[raw[write.Length + i]];
        return result;
    }

    // ------------------------------------------------------------------ I²C (24Cxx)
    static readonly int[] PageSizes = { 8, 8, 16, 16, 16, 32, 32, 64, 64, 128, 256 };

    static (byte dev, byte[] addr) I2cAddress(int id, int a)
    {
        if (id <= 4) return ((byte)(0xA0 | ((a >> 7) & 0x0E)), new[] { (byte)a });                    // 24C01..24C16: block bits in device address
        if (id >= 10) return ((byte)(0xA0 | ((a >> 15) & 0x02)), new[] { (byte)(a >> 8), (byte)a });  // 24C1024: A16 in device address
        return (0xA0, new[] { (byte)(a >> 8), (byte)a });
    }

    void NoteI2c()
    {
        if (_i2cNoted) return;
        _i2cNoted = true;
        Log?.Invoke("I²C over the raw USB protocol is experimental — verify every EEPROM write.");
    }

    static byte[] Packet(List<byte> cmds)
    {
        var p = new byte[PKT];
        for (int i = 0; i < cmds.Count; i++) p[i] = cmds[i];
        return p;
    }

    public void I2cRead(int eepromId, int addr, byte[] buffer, int offset, int len)
    {
        if (!IsOpen) throw new ProgrammerException("Programmer is not open.");
        NoteI2c();
        int done = 0;
        while (done < len)
        {
            int a = addr + done;
            int n = Math.Min(16 - (a & 15), len - done);
            var (dev, ab) = I2cAddress(eepromId, a);
            var c = new List<byte> { CMD_I2C_STREAM, I2C_STM_STA, (byte)(I2C_STM_OUT | (1 + ab.Length)), dev };
            c.AddRange(ab);
            c.Add(I2C_STM_STA);
            c.Add(I2C_STM_OUT | 1);
            c.Add((byte)(dev | 1));
            if (n > 1) c.Add((byte)(I2C_STM_IN | (n - 1)));   // n-1 bytes with ACK
            c.Add(I2C_STM_IN);                                // last byte with NACK
            c.Add(I2C_STM_STO);
            c.Add(I2C_STM_END);
            byte[] r;
            try { r = Exchange(Packet(c), n); }
            catch (ProgrammerException ex) { throw new ProgrammerException($"I²C read at 0x{a:X} failed — no EEPROM answering? ({ex.Message})"); }
            Buffer.BlockCopy(r, 0, buffer, offset + done, n);
            done += n;
        }
    }

    /// <summary>EEPROM internal write cycle (tWR ≤ 5 ms).</summary>
    protected virtual void WaitWriteCycle() => Thread.Sleep(8);

    public void I2cWrite(int eepromId, int addr, byte[] buffer, int offset, int len)
    {
        if (!IsOpen) throw new ProgrammerException("Programmer is not open.");
        NoteI2c();
        int page = PageSizes[Math.Clamp(eepromId, 0, PageSizes.Length - 1)];
        int done = 0;
        while (done < len)
        {
            int a = addr + done;
            int n = Math.Min(Math.Min(page - (a % page), 16), len - done);
            var (dev, ab) = I2cAddress(eepromId, a);
            var c = new List<byte> { CMD_I2C_STREAM, I2C_STM_STA, (byte)(I2C_STM_OUT | (1 + ab.Length + n)), dev };
            c.AddRange(ab);
            for (int i = 0; i < n; i++) c.Add(buffer[offset + done + i]);
            c.Add(I2C_STM_STO);
            c.Add(I2C_STM_END);
            BulkWrite(Packet(c), PKT);
            WaitWriteCycle();
            done += n;
        }
    }
}
