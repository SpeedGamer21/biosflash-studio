namespace BiosFlashStudio.Hardware;

public enum BackendKind { Auto, Ch341Dll, WinUsb, LibUsb, Simulator }

/// <summary>
/// Low level programmer abstraction. All SPI transactions are full-duplex:
/// CS is asserted, <c>write</c> is clocked out, then <c>readLen</c> bytes are clocked in, then CS is released.
/// </summary>
public interface IProgrammer : IDisposable
{
    string Name { get; }
    string Backend { get; }
    string DriverInfo { get; }
    string DeviceId { get; }
    bool IsOpen { get; }

    /// <summary>Maximum number of bytes (command + data) in a single SPI transaction.</summary>
    int MaxTransfer { get; }

    void Open();
    void Close();
    void SetSpeed(int speed);
    byte[] SpiTransfer(byte[] write, int readLen);

    bool SupportsI2c { get; }
    void I2cRead(int eepromId, int addr, byte[] buffer, int offset, int len);
    void I2cWrite(int eepromId, int addr, byte[] buffer, int offset, int len);
}

public sealed class ProgrammerException : Exception
{
    public ProgrammerException(string message) : base(message) { }
}

internal static class BitRev
{
    public static readonly byte[] Table = Build();
    static byte[] Build()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int b = i, r = 0;
            for (int k = 0; k < 8; k++) { r = (r << 1) | (b & 1); b >>= 1; }
            t[i] = (byte)r;
        }
        return t;
    }
}
