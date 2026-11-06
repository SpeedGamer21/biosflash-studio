using BiosFlashStudio.Hardware;

namespace BiosFlashStudio.Flash;

/// <summary>Thread-safe progress state shared between a worker and the UI timer.</summary>
public sealed class OpProgress
{
    long _done, _total, _addr;
    volatile string _stage = "";
    public long Done => Interlocked.Read(ref _done);
    public long Total => Interlocked.Read(ref _total);
    public long Address => Interlocked.Read(ref _addr);
    public string Stage { get => _stage; set => _stage = value; }

    public void Begin(string stage, long total)
    {
        _stage = stage;
        Interlocked.Exchange(ref _total, total);
        Interlocked.Exchange(ref _done, 0);
        Interlocked.Exchange(ref _addr, 0);
    }
    public void Set(long done, long address)
    {
        Interlocked.Exchange(ref _done, done);
        Interlocked.Exchange(ref _addr, address);
    }
    public void Reset() => Begin("", 0);
}

public sealed class VerifyResult
{
    public long Mismatches;
    public List<long> FirstAddresses = new();
    public bool Ok => Mismatches == 0;
}

/// <summary>Generic 25-series SPI NOR flash driver.</summary>
public sealed class SpiFlash
{
    readonly IProgrammer _p;
    public ChipInfo Chip { get; set; }
    public Action<string> Log { get; set; }
    public bool Allow4ByteMode { get; set; } = true;
    bool _4b;

    public SpiFlash(IProgrammer p, ChipInfo chip) { _p = p; Chip = chip; }

    byte[] X(byte[] w, int r = 0) => _p.SpiTransfer(w, r);

    // ---------------- identification ----------------
    public uint ReadJedecId()
    {
        var r = X(new byte[] { 0x9F }, 3);
        return (uint)(r[0] << 16 | r[1] << 8 | r[2]);
    }
    public byte ReadRes() => X(new byte[] { 0xAB, 0, 0, 0 }, 1)[0];
    public byte[] ReadRems() => X(new byte[] { 0x90, 0, 0, 0 }, 2);
    public byte[] ReadUniqueId() => X(new byte[] { 0x4B, 0, 0, 0, 0 }, 8);
    public byte[] ReadSfdp(int addr, int len) => X(new byte[] { 0x5A, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr, 0 }, len);

    /// <summary>Returns flash size from the SFDP Basic Flash Parameter Table, or 0.</summary>
    public long ReadSfdpSize()
    {
        try
        {
            var h = ReadSfdp(0, 16);
            if (h[0] != 'S' || h[1] != 'F' || h[2] != 'D' || h[3] != 'P') return 0;
            int ptp = h[12] | h[13] << 8 | h[14] << 16;
            var t = ReadSfdp(ptp, 8);
            uint d2 = BitConverter.ToUInt32(t, 4);
            long bits = (d2 & 0x80000000) == 0 ? d2 + 1L : (d2 & 0x7FFFFFFF) < 40 ? 1L << (int)(d2 & 0x7FFFFFFF) : 0;
            long bytes = bits / 8;
            return bytes is >= 64 * 1024 and <= 1L << 30 ? bytes : 0;
        }
        catch { return 0; }
    }

    // ---------------- status ----------------
    public byte ReadSr1() => X(new byte[] { 0x05 }, 1)[0];
    public byte ReadSr2() => X(new byte[] { 0x35 }, 1)[0];
    public byte ReadSr3() => X(new byte[] { 0x15 }, 1)[0];
    public void WriteEnable() => X(new byte[] { 0x06 });
    public void WriteDisable() => X(new byte[] { 0x04 });

    public void WriteSr1(byte v)
    {
        WriteEnable();
        X(new byte[] { 0x01, v });
        WaitReady(2000, CancellationToken.None);
    }

    public void WaitReady(int timeoutMs, CancellationToken ct, int sleepMs = 0)
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if ((ReadSr1() & 1) == 0) return;
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > end) throw new ProgrammerException("Timeout waiting for chip (BUSY bit stuck).");
            if (sleepMs > 0) Thread.Sleep(sleepMs);
        }
    }

    /// <summary>Clears block-protection bits. Returns the new SR1.</summary>
    public byte Unprotect()
    {
        if (Chip.Flags.HasFlag(ChipFlags.GlobalUnlock))
        {
            WriteEnable();
            X(new byte[] { 0x98 });
            WaitReady(1000, CancellationToken.None);
        }
        byte sr = ReadSr1();
        if ((sr & 0xFC) != 0)
        {
            Log?.Invoke($"Status register 0x{sr:X2} has protection bits set - clearing");
            WriteEnable();
            X(new byte[] { 0x50 });            // EWSR (SST), ignored by others
            WriteEnable();
            X(new byte[] { 0x01, 0x00 });
            WaitReady(2000, CancellationToken.None);
            sr = ReadSr1();
            if ((sr & 0x1C) != 0) Log?.Invoke($"Warning: protection bits still set (SR1=0x{sr:X2}). WP# pin may be held low.");
        }
        return sr;
    }

    // ---------------- addressing ----------------
    public void Begin()
    {
        if (Chip.NeedsFourByte && Allow4ByteMode && !_4b)
        {
            WriteEnable();
            X(new byte[] { 0xB7 });
            _4b = true;
        }
    }

    public void End()
    {
        if (_4b)
        {
            try { WriteEnable(); X(new byte[] { 0xE9 }); WriteDisable(); } catch { }
            _4b = false;
        }
    }

    byte[] Cmd(byte op, long addr, int extra)
    {
        int ab = _4b ? 4 : 3;
        var b = new byte[1 + ab + extra];
        b[0] = op;
        if (_4b) { b[1] = (byte)(addr >> 24); b[2] = (byte)(addr >> 16); b[3] = (byte)(addr >> 8); b[4] = (byte)addr; }
        else { b[1] = (byte)(addr >> 16); b[2] = (byte)(addr >> 8); b[3] = (byte)addr; }
        return b;
    }

    int ReadChunk => Math.Max(256, (_p.MaxTransfer - 8) / 256 * 256);

    // ---------------- read ----------------
    public byte[] Read(long addr, long len, CancellationToken ct, Action<long, long> progress)
    {
        var data = new byte[len];
        int chunk = ReadChunk;
        long done = 0;
        while (done < len)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk, len - done);
            var r = X(Cmd(0x03, addr + done, 0), n);
            Buffer.BlockCopy(r, 0, data, (int)done, n);
            done += n;
            progress?.Invoke(done, addr + done);
        }
        return data;
    }

    public VerifyResult Verify(long addr, byte[] expected, long offset, long len, CancellationToken ct, Action<long, long> progress)
    {
        var res = new VerifyResult();
        int chunk = ReadChunk;
        long done = 0;
        while (done < len)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk, len - done);
            var r = X(Cmd(0x03, addr + done, 0), n);
            for (int i = 0; i < n; i++)
            {
                if (r[i] != expected[offset + done + i])
                {
                    res.Mismatches++;
                    if (res.FirstAddresses.Count < 16) res.FirstAddresses.Add(addr + done + i);
                }
            }
            done += n;
            progress?.Invoke(done, addr + done);
        }
        return res;
    }

    // ---------------- erase ----------------
    public void EraseChip(CancellationToken ct, Action<double> fraction)
    {
        WriteEnable();
        X(new byte[] { 0xC7 });
        double expectedMs = Math.Max(4000, Chip.Size / (1024.0 * 1024.0) * 3500);
        long start = Environment.TickCount64;
        long timeout = (long)Math.Max(120_000, expectedMs * 8);
        while ((ReadSr1() & 1) != 0)
        {
            ct.ThrowIfCancellationRequested();
            long el = Environment.TickCount64 - start;
            if (el > timeout) throw new ProgrammerException("Chip erase timeout.");
            fraction?.Invoke(Math.Min(0.97, el / expectedMs));
            Thread.Sleep(100);
        }
        fraction?.Invoke(1.0);
    }

    public void EraseSector(long addr)
    {
        WriteEnable();
        X(Cmd(0x20, addr, 0));
        WaitReady(3000, CancellationToken.None);
    }

    public void EraseBlock64(long addr)
    {
        WriteEnable();
        X(Cmd(0xD8, addr, 0));
        WaitReady(10000, CancellationToken.None);
    }

    /// <summary>Erases [addr, addr+len) rounded out to 4 KB sectors, using 64 KB blocks where possible.</summary>
    public void EraseRange(long addr, long len, bool sectorsOnly, CancellationToken ct, Action<long, long> progress)
    {
        long start = addr & ~0xFFFL;
        long end = (addr + len + 0xFFF) & ~0xFFFL;
        long a = start;
        while (a < end)
        {
            ct.ThrowIfCancellationRequested();
            if (!sectorsOnly && (a & 0xFFFF) == 0 && end - a >= 0x10000) { EraseBlock64(a); a += 0x10000; }
            else { EraseSector(a); a += 0x1000; }
            progress?.Invoke(a - start, a);
        }
    }

    // ---------------- program ----------------
    public void Program(long addr, byte[] data, long offset, long len, bool skipBlank, CancellationToken ct, Action<long, long> progress)
    {
        if (Chip.Flags.HasFlag(ChipFlags.Aai)) { ProgramAai(addr, data, offset, len, ct, progress); return; }
        int page = Chip.PageSize <= 0 ? 256 : Chip.PageSize;
        long done = 0;
        while (done < len)
        {
            ct.ThrowIfCancellationRequested();
            long a = addr + done;
            int n = (int)Math.Min(page - (a % page), len - done);
            if (!(skipBlank && IsBlank(data, offset + done, n)))
            {
                var c = Cmd(0x02, a, n);
                Buffer.BlockCopy(data, (int)(offset + done), c, c.Length - n, n);
                WriteEnable();
                X(c);
                WaitReady(1000, ct);
            }
            done += n;
            progress?.Invoke(done, addr + done);
        }
    }

    void ProgramAai(long addr, byte[] data, long offset, long len, CancellationToken ct, Action<long, long> progress)
    {
        // SST25VF AAI word program (address must be even)
        long done = 0;
        bool first = true;
        WriteEnable();
        while (done < len)
        {
            ct.ThrowIfCancellationRequested();
            byte b0 = data[offset + done];
            byte b1 = done + 1 < len ? data[offset + done + 1] : (byte)0xFF;
            if (first) { X(new byte[] { 0xAD, (byte)((addr + done) >> 16), (byte)((addr + done) >> 8), (byte)(addr + done), b0, b1 }); first = false; }
            else X(new byte[] { 0xAD, b0, b1 });
            WaitReady(100, ct);
            done += 2;
            if ((done & 0xFFF) == 0) progress?.Invoke(Math.Min(done, len), addr + done);
        }
        WriteDisable();
        WaitReady(100, ct);
        progress?.Invoke(len, addr + len);
    }

    public static bool IsBlank(byte[] d, long off, long len)
    {
        var span = new ReadOnlySpan<byte>(d, (int)off, (int)len);
        return span.IndexOfAnyExcept((byte)0xFF) < 0;
    }

    /// <summary>
    /// Smart write: only erases/programs 4 KB sectors that actually differ from the buffer.
    /// Returns (sectorsSkipped, sectorsErased, sectorsProgrammed).
    /// </summary>
    public (int skipped, int erased, int written) SmartProgram(long addr, byte[] data, long len, CancellationToken ct, Action<long, long> progress)
    {
        int skipped = 0, erased = 0, written = 0;
        const int SEC = 4096;
        long done = 0;
        while (done < len)
        {
            ct.ThrowIfCancellationRequested();
            long a = addr + done;
            int n = (int)Math.Min(SEC, len - done);
            var cur = Read(a, n, ct, null);
            var want = new ReadOnlySpan<byte>(data, (int)done, n);
            if (want.SequenceEqual(cur)) { skipped++; }
            else
            {
                bool needErase = false;
                for (int i = 0; i < n; i++) if ((cur[i] & want[i]) != want[i]) { needErase = true; break; }
                if (needErase) { EraseSector(a); erased++; Array.Fill(cur, (byte)0xFF); }
                int page = Chip.PageSize <= 0 ? 256 : Chip.PageSize;
                for (int p = 0; p < n; p += page)
                {
                    int pn = Math.Min(page, n - p);
                    if (want.Slice(p, pn).SequenceEqual(new ReadOnlySpan<byte>(cur, p, pn))) continue;
                    Program(a + p, data, done + p, pn, true, ct, null);
                }
                written++;
            }
            done += n;
            progress?.Invoke(done, addr + done);
        }
        return (skipped, erased, written);
    }
}

/// <summary>24xx I2C EEPROM operations.</summary>
public sealed class I2cEeprom
{
    readonly IProgrammer _p;
    public ChipInfo Chip { get; }
    public I2cEeprom(IProgrammer p, ChipInfo chip) { _p = p; Chip = chip; }

    public byte[] Read(CancellationToken ct, Action<long, long> progress)
    {
        var d = new byte[Chip.Size];
        const int CH = 256;
        for (int a = 0; a < Chip.Size; a += CH)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(CH, Chip.Size - a);
            _p.I2cRead(Chip.EepromId, a, d, a, n);
            progress?.Invoke(a + n, a + n);
        }
        return d;
    }

    public void Write(byte[] data, CancellationToken ct, Action<long, long> progress)
    {
        int ch = Math.Max(Chip.PageSize, 16);
        long len = Math.Min(data.Length, Chip.Size);
        for (int a = 0; a < len; a += ch)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(ch, len - a);
            _p.I2cWrite(Chip.EepromId, a, data, a, n);
            progress?.Invoke(a + n, a + n);
        }
    }
}
