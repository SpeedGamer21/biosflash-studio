using System.Runtime.InteropServices;

namespace BiosFlashStudio.Hardware;

/// <summary>
/// Minimal dynamic binding to libusb-1.0 (Linux: libusb-1.0.so.0 from the libusb-1.0-0 / libusb1 package).
/// Loaded at runtime so the application still starts when libusb is missing.
/// </summary>
internal static unsafe class LibUsb
{
    static IntPtr _lib;
    static string _err;
    public static string LibraryPath { get; private set; } = "";

    public static delegate* unmanaged[Cdecl]<IntPtr*, int> Init;
    public static delegate* unmanaged[Cdecl]<IntPtr, void> Exit;
    public static delegate* unmanaged[Cdecl]<IntPtr, ushort, ushort, IntPtr> OpenVidPid;
    public static delegate* unmanaged[Cdecl]<IntPtr, void> CloseDev;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, int> SetAutoDetach;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, int> KernelDriverActive;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, int> DetachKernelDriver;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, int> ClaimInterface;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, int> ReleaseInterface;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte, byte*, int, int*, uint, int> BulkTransfer;
    public static delegate* unmanaged[Cdecl]<int, IntPtr> ErrorName;

    static readonly string[] Candidates =
    {
        "libusb-1.0.so.0", "libusb-1.0.so", "/usr/lib/x86_64-linux-gnu/libusb-1.0.so.0", "/usr/lib64/libusb-1.0.so.0",
        "/usr/lib/aarch64-linux-gnu/libusb-1.0.so.0", "/usr/lib/libusb-1.0.so.0", "libusb-1.0.0.dylib", "libusb-1.0.dll",
    };

    /// <summary>Returns null on success, otherwise an error message.</summary>
    public static string TryLoad()
    {
        if (_lib != IntPtr.Zero) return null;
        if (_err != null) return _err;
        foreach (var c in Candidates)
        {
            if (!NativeLibrary.TryLoad(c, out var h)) continue;
            try
            {
                Init = (delegate* unmanaged[Cdecl]<IntPtr*, int>)NativeLibrary.GetExport(h, "libusb_init");
                Exit = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(h, "libusb_exit");
                OpenVidPid = (delegate* unmanaged[Cdecl]<IntPtr, ushort, ushort, IntPtr>)NativeLibrary.GetExport(h, "libusb_open_device_with_vid_pid");
                CloseDev = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(h, "libusb_close");
                SetAutoDetach = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(h, "libusb_set_auto_detach_kernel_driver");
                KernelDriverActive = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(h, "libusb_kernel_driver_active");
                DetachKernelDriver = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(h, "libusb_detach_kernel_driver");
                ClaimInterface = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(h, "libusb_claim_interface");
                ReleaseInterface = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(h, "libusb_release_interface");
                BulkTransfer = (delegate* unmanaged[Cdecl]<IntPtr, byte, byte*, int, int*, uint, int>)NativeLibrary.GetExport(h, "libusb_bulk_transfer");
                ErrorName = (delegate* unmanaged[Cdecl]<int, IntPtr>)NativeLibrary.GetExport(h, "libusb_error_name");
                _lib = h;
                LibraryPath = c;
                return null;
            }
            catch (Exception ex)
            {
                NativeLibrary.Free(h);
                _err = $"{c} is missing exports ({ex.Message})";
            }
        }
        _err ??= OperatingSystem.IsWindows()
            ? "libusb-1.0.dll not found (only needed for the libusb backend on Windows)."
            : "libusb-1.0 not found. Install it:  Debian/Ubuntu: sudo apt install libusb-1.0-0   •   Fedora: sudo dnf install libusb1";
        return _err;
    }

    public static string Err(int code)
    {
        try { return Marshal.PtrToStringAnsi(ErrorName(code)) ?? code.ToString(); }
        catch { return code.ToString(); }
    }
}

/// <summary>Linux transport: raw CH341A protocol through libusb-1.0.</summary>
public sealed unsafe class Ch341LibUsbProgrammer : Ch341DirectProgrammer
{
    const uint Timeout = 5000;
    readonly UsbDeviceInfo _dev;
    IntPtr _ctx, _h;
    bool _claimed;

    public override string Name => "CH341A";
    public override string Backend => "libusb";
    public override string DriverInfo => $"libusb-1.0 ({LibUsb.LibraryPath})";
    public override string DeviceId => _dev?.InstanceId ?? "1a86:5512";

    public Ch341LibUsbProgrammer(UsbDeviceInfo dev) { _dev = dev; }

    protected override void TransportOpen()
    {
        var err = LibUsb.TryLoad();
        if (err != null) throw new ProgrammerException(err);
        IntPtr ctx;
        int rc = LibUsb.Init(&ctx);
        if (rc != 0) throw new ProgrammerException("libusb_init failed: " + LibUsb.Err(rc));
        _ctx = ctx;
        _h = LibUsb.OpenVidPid(_ctx, 0x1A86, 0x5512);
        if (_h == IntPtr.Zero)
        {
            LibUsb.Exit(_ctx);
            _ctx = IntPtr.Zero;
            throw new ProgrammerException(OperatingSystem.IsLinux()
                ? "Cannot open the CH341A (1a86:5512). If it is plugged in this is a permission problem — install the udev rule (Diagnostics → Install udev rule) and re-plug the programmer."
                : "Cannot open the CH341A (1a86:5512) with libusb.");
        }
        LibUsb.SetAutoDetach(_h, 1);
        if (LibUsb.KernelDriverActive(_h, 0) == 1)
        {
            rc = LibUsb.DetachKernelDriver(_h, 0);
            if (rc != 0) Log?.Invoke("Could not detach kernel driver: " + LibUsb.Err(rc));
        }
        rc = LibUsb.ClaimInterface(_h, 0);
        if (rc != 0)
        {
            TransportClose();
            throw new ProgrammerException($"libusb_claim_interface failed ({LibUsb.Err(rc)}). Another program (flashrom, a kernel spi/i2c-ch341 module) may be using the programmer.");
        }
        _claimed = true;
        // drain any stale input
        var junk = new byte[64];
        fixed (byte* p = junk) { int t; LibUsb.BulkTransfer(_h, EP_IN, p, 64, &t, 20); }
    }

    protected override void TransportClose()
    {
        if (_h != IntPtr.Zero)
        {
            if (_claimed) LibUsb.ReleaseInterface(_h, 0);
            LibUsb.CloseDev(_h);
        }
        if (_ctx != IntPtr.Zero) LibUsb.Exit(_ctx);
        _h = _ctx = IntPtr.Zero;
        _claimed = false;
    }

    protected override void BulkWrite(byte[] data, int len)
    {
        int sent;
        int rc;
        fixed (byte* p = data) rc = LibUsb.BulkTransfer(_h, EP_OUT, p, len, &sent, Timeout);
        if (rc != 0 || sent != len) throw new ProgrammerException($"USB write failed ({(rc != 0 ? LibUsb.Err(rc) : $"{sent}/{len} bytes")}).");
    }

    protected override byte[] Exchange(byte[] outBuf, int inLen)
    {
        // The CH341 answers each SPI/I²C packet with a short IN packet, so the IN side has to be
        // drained while the OUT data is still being sent: write on a worker, read here.
        var writer = Task.Run(() => BulkWrite(outBuf, outBuf.Length));
        int cap = (inLen + PKT - 1) / PKT * PKT + PKT;
        var buf = new byte[cap];
        int got = 0;
        string readErr = null;
        fixed (byte* p = buf)
        {
            while (got < inLen)
            {
                int want = Math.Min(cap - got, (inLen - got + PKT - 1) / PKT * PKT);
                int n;
                int rc = LibUsb.BulkTransfer(_h, EP_IN, p + got, want, &n, Timeout);
                if (rc != 0) { readErr = rc == -7 ? "USB read timeout - programmer not responding" : "USB read failed: " + LibUsb.Err(rc); break; }
                got += n;
            }
        }
        try { writer.Wait(); }
        catch (AggregateException ae) { throw ae.InnerException ?? ae; }
        if (readErr != null) throw new ProgrammerException(readErr + $" ({got}/{inLen}).");
        return buf;
    }
}
