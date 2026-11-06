using System.Runtime.InteropServices;

namespace BiosFlashStudio.Hardware;

/// <summary>
/// CH341A backend using the official WCH vendor driver (CH341PAR) and its DLL
/// (CH341DLLA64.DLL for 64-bit processes, CH341DLL.DLL for 32-bit) - the same route AsProgrammer uses.
/// </summary>
public sealed class Ch341DllProgrammer : IProgrammer
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr OpenDeviceFn(uint idx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CloseDeviceFn(uint idx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint GetVersionFn();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint GetVerIcFn(uint idx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetStreamFn(uint idx, uint mode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StreamSpi4Fn(uint idx, uint chipSelect, uint length, [In, Out] byte[] buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EepromFn(uint idx, int eepromId, uint addr, uint len, [In, Out] byte[] buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetTimeoutFn(uint idx, uint writeTimeout, uint readTimeout);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetDeviceNameFn(uint idx);

    static IntPtr _lib;
    static string _libPath;
    static OpenDeviceFn _open;
    static CloseDeviceFn _close;
    static GetVersionFn _getVersion, _getDrvVersion;
    static GetVerIcFn _getVerIc;
    static SetStreamFn _setStream;
    static StreamSpi4Fn _spi4;
    static EepromFn _readEeprom, _writeEeprom;
    static SetTimeoutFn _setTimeout;
    static GetDeviceNameFn _getDeviceName;

    readonly uint _index;
    int _speed = 1;
    public bool IsOpen { get; private set; }
    public string Name { get; private set; } = "CH341A";
    public string Backend => "CH341DLL (WCH vendor driver)";
    public string DriverInfo { get; private set; } = "";
    public string DeviceId { get; private set; } = "";
    public int MaxTransfer => 4096;
    public bool SupportsI2c => true;
    public static string LibraryPath => _libPath;

    public Ch341DllProgrammer(uint index = 0) { _index = index; }

    public static string DllName => Environment.Is64BitProcess ? "CH341DLLA64.DLL" : "CH341DLL.DLL";

    /// <summary>Loads the WCH DLL. Returns null on success or an error message.</summary>
    public static string TryLoadLibrary()
    {
        if (_lib != IntPtr.Zero) return null;
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, DllName),
            Path.Combine(AppContext.BaseDirectory, "drivers", DllName),
            DllName,
        };
        foreach (var c in candidates)
        {
            if (NativeLibrary.TryLoad(c, out var h))
            {
                _lib = h;
                _libPath = c.Contains('\\') ? c : Path.Combine(Environment.SystemDirectory, c);
                break;
            }
        }
        if (_lib == IntPtr.Zero)
            return $"{DllName} not found. Install the WCH CH341PAR driver (or copy {DllName} next to BiosFlashStudio.exe).";
        try
        {
            _open = Get<OpenDeviceFn>("CH341OpenDevice");
            _close = Get<CloseDeviceFn>("CH341CloseDevice");
            _getVersion = Get<GetVersionFn>("CH341GetVersion");
            _getDrvVersion = Get<GetVersionFn>("CH341GetDrvVersion");
            _getVerIc = Get<GetVerIcFn>("CH341GetVerIC");
            _setStream = Get<SetStreamFn>("CH341SetStream");
            _spi4 = Get<StreamSpi4Fn>("CH341StreamSPI4");
            _readEeprom = Get<EepromFn>("CH341ReadEEPROM");
            _writeEeprom = Get<EepromFn>("CH341WriteEEPROM");
            _setTimeout = Get<SetTimeoutFn>("CH341SetTimeout");
            _getDeviceName = Get<GetDeviceNameFn>("CH341GetDeviceName");
        }
        catch (Exception ex)
        {
            _lib = IntPtr.Zero;
            return "CH341 DLL is missing required exports: " + ex.Message;
        }
        return null;
    }

    static T Get<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));

    public void Open()
    {
        if (IsOpen) return;
        var err = TryLoadLibrary();
        if (err != null) throw new ProgrammerException(err);
        var h = _open(_index);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
            throw new ProgrammerException("CH341A not found by the WCH driver (CH341OpenDevice failed). Check the USB connection and that the programmer jumper is in SPI/I2C mode.");
        IsOpen = true;
        uint ic = _getVerIc(_index);
        Name = ic switch { 0x10 => "CH341", 0x20 => "CH341A", 0x30 => "CH341A3", _ => $"CH341 (IC 0x{ic:X2})" };
        DriverInfo = $"DLL v{_getVersion()} / Driver v{_getDrvVersion()}";
        try
        {
            var p = _getDeviceName(_index);
            DeviceId = p != IntPtr.Zero ? Marshal.PtrToStringAnsi(p) ?? "" : "";
        }
        catch { DeviceId = ""; }
        _setTimeout(_index, 2000, 2000);
        ApplyStream();
    }

    void ApplyStream()
    {
        // bit7 = MSB first (SPI), bit2 = 0 single SPI, bits1..0 = I2C clock (20k/100k/400k/750k)
        if (_setStream(_index, 0x80u | (uint)(_speed & 3)) == 0)
            throw new ProgrammerException("CH341SetStream failed.");
    }

    public void SetSpeed(int speed)
    {
        _speed = Math.Clamp(speed, 0, 3);
        if (IsOpen) ApplyStream();
    }

    public byte[] SpiTransfer(byte[] write, int readLen)
    {
        if (!IsOpen) throw new ProgrammerException("Programmer is not open.");
        int total = write.Length + readLen;
        if (total > MaxTransfer) throw new ArgumentException("SPI transfer too large");
        var buf = new byte[total];
        Buffer.BlockCopy(write, 0, buf, 0, write.Length);
        for (int i = write.Length; i < total; i++) buf[i] = 0xFF;
        // 0x80 = enable chip-select control, D0 is CS
        if (_spi4(_index, 0x80, (uint)total, buf) == 0)
            throw new ProgrammerException("CH341StreamSPI4 failed (device disconnected?)");
        var r = new byte[readLen];
        Buffer.BlockCopy(buf, write.Length, r, 0, readLen);
        return r;
    }

    public void I2cRead(int eepromId, int addr, byte[] buffer, int offset, int len)
    {
        var tmp = new byte[len];
        if (_readEeprom(_index, eepromId, (uint)addr, (uint)len, tmp) == 0)
            throw new ProgrammerException($"I2C EEPROM read failed at 0x{addr:X} (no ACK - check chip type/orientation).");
        Buffer.BlockCopy(tmp, 0, buffer, offset, len);
    }

    public void I2cWrite(int eepromId, int addr, byte[] buffer, int offset, int len)
    {
        var tmp = new byte[len];
        Buffer.BlockCopy(buffer, offset, tmp, 0, len);
        if (_writeEeprom(_index, eepromId, (uint)addr, (uint)len, tmp) == 0)
            throw new ProgrammerException($"I2C EEPROM write failed at 0x{addr:X} (write-protect pin?).");
    }

    public void Close()
    {
        if (!IsOpen) return;
        try { _close(_index); } catch { }
        IsOpen = false;
    }

    public void Dispose() => Close();
}
