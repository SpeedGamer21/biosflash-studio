using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BiosFlashStudio.Hardware;

/// <summary>
/// Windows transport: raw CH341A protocol through WinUSB (driver installed with Zadig).
/// </summary>
public sealed class Ch341WinUsbProgrammer : Ch341DirectProgrammer
{
    #region native
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint PIPE_TRANSFER_TIMEOUT = 0x03, IGNORE_SHORT_PACKETS = 0x04;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_Initialize(SafeFileHandle dev, out IntPtr h);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_Free(IntPtr h);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_WritePipe(IntPtr h, byte pipe, byte[] buf, uint len, out uint done, IntPtr ov);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_ReadPipe(IntPtr h, byte pipe, IntPtr buf, uint len, out uint done, IntPtr ov);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_GetOverlappedResult(IntPtr h, IntPtr ov, out uint done, bool wait);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_SetPipePolicy(IntPtr h, byte pipe, uint policy, uint len, ref uint value);
    [DllImport("winusb.dll", SetLastError = true, EntryPoint = "WinUsb_SetPipePolicy")] static extern bool WinUsb_SetPipePolicyByte(IntPtr h, byte pipe, uint policy, uint len, ref byte value);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_AbortPipe(IntPtr h, byte pipe);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_FlushPipe(IntPtr h, byte pipe);
    #endregion

    readonly UsbDeviceInfo _dev;
    SafeFileHandle _file;
    IntPtr _wusb, _ov, _rbuf;
    int _rbufLen;
    ManualResetEvent _evt;

    public override string Name => "CH341A";
    public override string Backend => "WinUSB";
    public override string DriverInfo => "WinUSB (Zadig)";
    public override string DeviceId => _dev.InstanceId;

    public Ch341WinUsbProgrammer(UsbDeviceInfo dev) { _dev = dev; }

    protected override void TransportOpen()
    {
        var path = UsbScanner.GetInterfacePath(_dev)
                   ?? throw new ProgrammerException("WinUSB interface path for the CH341A was not found (re-plug the programmer or reinstall WinUSB with Zadig).");
        _file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (_file.IsInvalid) throw new ProgrammerException($"Cannot open CH341A (Win32 error {Marshal.GetLastWin32Error()}). Is another program using it?");
        if (!WinUsb_Initialize(_file, out _wusb))
        {
            _file.Dispose();
            throw new ProgrammerException($"WinUsb_Initialize failed (error {Marshal.GetLastWin32Error()}).");
        }
        uint timeout = 3000;
        WinUsb_SetPipePolicy(_wusb, EP_OUT, PIPE_TRANSFER_TIMEOUT, 4, ref timeout);
        WinUsb_SetPipePolicy(_wusb, EP_IN, PIPE_TRANSFER_TIMEOUT, 4, ref timeout);
        byte one = 1;
        WinUsb_SetPipePolicyByte(_wusb, EP_IN, IGNORE_SHORT_PACKETS, 1, ref one);
        WinUsb_FlushPipe(_wusb, EP_IN);
        _evt = new ManualResetEvent(false);
        _ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
    }

    protected override void TransportClose()
    {
        WinUsb_Free(_wusb);
        _file?.Dispose();
        if (_ov != IntPtr.Zero) { Marshal.FreeHGlobal(_ov); _ov = IntPtr.Zero; }
        if (_rbuf != IntPtr.Zero) { Marshal.FreeHGlobal(_rbuf); _rbuf = IntPtr.Zero; _rbufLen = 0; }
        _evt?.Dispose();
    }

    protected override void BulkWrite(byte[] data, int len)
    {
        if (!WinUsb_WritePipe(_wusb, EP_OUT, data, (uint)len, out uint done, IntPtr.Zero) || done != len)
            throw new ProgrammerException($"USB write failed (error {Marshal.GetLastWin32Error()}).");
    }

    protected override byte[] Exchange(byte[] outBuf, int total)
    {
        if (_rbufLen < total)
        {
            if (_rbuf != IntPtr.Zero) Marshal.FreeHGlobal(_rbuf);
            _rbuf = Marshal.AllocHGlobal(total);
            _rbufLen = total;
        }
        _evt.Reset();
        var ov = new NativeOverlapped { EventHandle = _evt.SafeWaitHandle.DangerousGetHandle() };
        Marshal.StructureToPtr(ov, _ov, false);
        if (!WinUsb_ReadPipe(_wusb, EP_IN, _rbuf, (uint)total, out _, _ov))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 997) throw new ProgrammerException($"USB read submit failed (error {err}).");
        }
        try
        {
            BulkWrite(outBuf, outBuf.Length);
        }
        catch
        {
            WinUsb_AbortPipe(_wusb, EP_IN);
            WinUsb_GetOverlappedResult(_wusb, _ov, out _, true);
            throw;
        }
        if (!_evt.WaitOne(5000))
        {
            WinUsb_AbortPipe(_wusb, EP_IN);
            WinUsb_GetOverlappedResult(_wusb, _ov, out _, true);
            throw new ProgrammerException("USB read timeout - programmer not responding.");
        }
        if (!WinUsb_GetOverlappedResult(_wusb, _ov, out uint got, false) || got < total)
            throw new ProgrammerException($"USB short read ({got}/{total}).");
        var r = new byte[total];
        Marshal.Copy(_rbuf, r, 0, total);
        return r;
    }
}
