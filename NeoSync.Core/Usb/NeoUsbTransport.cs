// Bulk USB transport for the AlphaSmart Neo in comms mode (VID=0x081e PID=0xbd01).
// Uses Windows-built-in WinUSB via P/Invoke: setupapi.dll, winusb.dll, kernel32.dll.
// No third-party packages.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NeoSync.Core.Usb;

[SupportedOSPlatform("windows")]
public sealed class NeoUsbTransport : INeoTransport
{
    // AlphaSmart Neo VID/PID in comms mode
    private const int CommsVendorId  = 0x081e;
    private const int CommsProductId = 0xbd01;

    private const int DefaultTimeoutMs = 20_000;

    private SafeFileHandle?   _deviceHandle;
    private nint              _winUsbHandle;
    private byte              _pipeIn;
    private byte              _pipeOut;
    private bool              _disposed;

    private NeoUsbTransport() { }

    /// <summary>Opens the Neo comms-mode device. Returns null if the device is not found.</summary>
    public static NeoUsbTransport? TryOpen() => TryOpen(out _);

    /// <summary>Opens the Neo comms-mode device, also returning a diagnostic string.</summary>
    public static NeoUsbTransport? TryOpen(out string diagnostics)
    {
        string? path = FindDevicePath();
        if (path is null)
        {
            diagnostics = "FindDevicePath returned null (device not found in registry/SetupDi).";
            return null;
        }

        diagnostics = $"Path found: {path}";
        var transport = new NeoUsbTransport();
        if (!transport.Open(path))
        {
            diagnostics = $"Open failed on [{path}]: {transport._lastOpenDiag}";
            transport.Dispose();
            return null;
        }
        return transport;
    }

    /// <summary>Returns the device path for the comms device, or null if not connected.</summary>
    internal static string? FindDevicePath()
        => FindDevicePathByVidPid(CommsVendorId, CommsProductId);

    // ── INeoTransport ────────────────────────────────────────────────────────

    public bool Read(byte[] buffer, int length, out int actual, int timeoutMs = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

        actual = 0;
        int offset = 0;
        int remaining = length;

        // The protocol sends/receives in 8-byte frames
        byte[] block = new byte[8];

        while (remaining > 0)
        {
            int blockSize = Math.Min(remaining, 8);
            uint transferred = 0;

            if (!NativeMethods.WinUsb_ReadPipe(_winUsbHandle, _pipeIn, block, (uint)blockSize, ref transferred, nint.Zero))
                return false;

            if (transferred == 0) break;

            Buffer.BlockCopy(block, 0, buffer, offset, (int)transferred);
            offset    += (int)transferred;
            remaining -= (int)transferred;
            actual    += (int)transferred;

            if (transferred < 8) break; // short read — end of data
        }

        return true;
    }

    public bool Write(byte[] data, int length, int timeoutMs = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

        int offset = 0;
        int remaining = length;

        while (remaining > 0)
        {
            int blockSize = Math.Min(remaining, 8);
            uint transferred = 0;

            // Write from a local block so we never hand a larger slice than needed
            byte[] block = new byte[blockSize];
            Buffer.BlockCopy(data, offset, block, 0, blockSize);

            if (!NativeMethods.WinUsb_WritePipe(_winUsbHandle, _pipeOut, block, (uint)blockSize, ref transferred, nint.Zero))
                return false;

            offset    += (int)transferred;
            remaining -= (int)transferred;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_winUsbHandle != nint.Zero)
        {
            NativeMethods.WinUsb_Free(_winUsbHandle);
            _winUsbHandle = nint.Zero;
        }

        _deviceHandle?.Dispose();
        _deviceHandle = null;
    }

    // ── Private setup ────────────────────────────────────────────────────────

    private bool Open(string devicePath)
    {
        _deviceHandle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            nint.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOverlapped,
            nint.Zero);

        if (_deviceHandle.IsInvalid)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _lastOpenDiag = $"CreateFile failed (Win32 {err})";
            DbgLog.W($"TryOpen.Open CreateFile failed path={devicePath} Win32={err}");
            return false;
        }

        if (!NativeMethods.WinUsb_Initialize(_deviceHandle, out _winUsbHandle))
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _lastOpenDiag = $"WinUsb_Initialize failed (Win32 {err})";
            DbgLog.W($"TryOpen.Open WinUsb_Initialize failed Win32={err}");
            return false;
        }

        if (!FindBulkPipes(out string pipeDiag))
        {
            _lastOpenDiag = $"FindBulkPipes failed: {pipeDiag}";
            NativeMethods.WinUsb_Free(_winUsbHandle);
            _winUsbHandle = nint.Zero;
            return false;
        }

        _lastOpenDiag = "OK";
        return true;
    }

    internal string _lastOpenDiag = string.Empty;

    private bool FindBulkPipes(out string diagnostics)
    {
        // Query interface descriptor to get the number of pipes
        var ifaceDesc = new NativeMethods.UsbInterfaceDescriptor();
        if (!NativeMethods.WinUsb_QueryInterfaceSettings(_winUsbHandle, 0, ref ifaceDesc))
        {
            diagnostics = $"WinUsb_QueryInterfaceSettings failed (Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})";
            return false;
        }

        byte pipeIn  = 0;
        byte pipeOut = 0;

        for (byte i = 0; i < ifaceDesc.bNumEndpoints; i++)
        {
            var pipeInfo = new NativeMethods.WinUsbPipeInformation();
            if (!NativeMethods.WinUsb_QueryPipe(_winUsbHandle, 0, i, ref pipeInfo))
                continue;

            if (pipeInfo.PipeType != NativeMethods.UsbdPipeTypeBulk)
                continue;

            // Bit 7 of the address: 1 = IN, 0 = OUT
            if ((pipeInfo.PipeId & 0x80) != 0 && pipeIn  == 0) pipeIn  = pipeInfo.PipeId;
            else if ((pipeInfo.PipeId & 0x80) == 0 && pipeOut == 0) pipeOut = pipeInfo.PipeId;
        }

        if (pipeIn == 0 || pipeOut == 0)
        {
            diagnostics = $"No bulk IN/OUT pair found (endpoints={ifaceDesc.bNumEndpoints}, pipeIn={pipeIn:x2}, pipeOut={pipeOut:x2})";
            return false;
        }

        _pipeIn  = pipeIn;
        _pipeOut = pipeOut;
        diagnostics = $"OK (pipeIn={pipeIn:x2}, pipeOut={pipeOut:x2})";
        return true;
    }

    internal static string? FindDevicePathByVidPid(int vendorId, int productId)
    {
        // WinUSB installers (Zadig etc.) store a per-device interface GUID under the
        // registry Device Parameters key. Read it, then use it for SetupDi enumeration.
        Guid? ifaceGuid = ReadWinUsbInterfaceGuid(vendorId, productId);
        if (ifaceGuid is null) return null;

        Guid guid = ifaceGuid.Value;

        nint devInfo = NativeMethods.SetupDiGetClassDevs(
            ref guid,
            null,
            nint.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

        if (devInfo == NativeMethods.InvalidHandleValue) return null;

        try
        {
            var devInterfaceData = new NativeMethods.SpDeviceInterfaceData();
            devInterfaceData.cbSize = Marshal.SizeOf<NativeMethods.SpDeviceInterfaceData>();

            for (int i = 0; NativeMethods.SetupDiEnumDeviceInterfaces(devInfo, nint.Zero, ref guid, i, ref devInterfaceData); i++)
            {
                string? path = GetDevicePath(devInfo, ref devInterfaceData);
                if (path is null) continue;

                string lower = path.ToLowerInvariant();
                if (lower.Contains($"vid_{vendorId:x4}") && lower.Contains($"pid_{productId:x4}"))
                    return path;
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(devInfo);
        }

        return null;
    }

    /// <summary>
    /// Reads the WinUSB device interface GUID from the registry entry that WinUSB
    /// installers (such as Zadig) write to Device Parameters\DeviceInterfaceGUIDs.
    /// </summary>
    private static Guid? ReadWinUsbInterfaceGuid(int vendorId, int productId)
    {
        string vidPid = $"VID_{vendorId:X4}&PID_{productId:X4}";

        using RegistryKey? usbKey =
            Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usbKey is null) return null;

        foreach (string devId in usbKey.GetSubKeyNames())
        {
            if (!devId.Contains(vidPid, StringComparison.OrdinalIgnoreCase)) continue;

            using RegistryKey? devKey = usbKey.OpenSubKey(devId);
            if (devKey is null) continue;

            foreach (string instId in devKey.GetSubKeyNames())
            {
                using RegistryKey? paramsKey =
                    devKey.OpenSubKey($@"{instId}\Device Parameters");
                if (paramsKey is null) continue;

                if (paramsKey.GetValue("DeviceInterfaceGUIDs") is string[] guids
                    && guids.Length > 0
                    && Guid.TryParse(guids[0].Trim('{', '}'), out Guid g))
                {
                    return g;
                }
            }
        }

        return null;
    }


    private static string? GetDevicePath(nint devInfo, ref NativeMethods.SpDeviceInterfaceData interfaceData)
        => NativeMethods.GetDeviceInterfacePath(devInfo, ref interfaceData);
}

