// Switches the AlphaSmart Neo from HID keyboard mode (PID=0xBD04) to
// bulk comms mode (PID=0xBD01) by sending five USB control transfers.
//
// Protocol confirmed against neotools (Linux, C:\repos\neotools\neotools\device.py
// flip_to_comms_mode()): bmRequestType=0x20 (Class|Device - NOT Interface),
// bRequest=9 (SET_REPORT), wValue=0x0200, wIndex=1, one byte per transfer
// (0xE0..0xE4), preceded by SET_CONFIGURATION. BD04 has only one real USB
// interface - there is no MI_01 to split out via a composite driver.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NeoSync.Core.Usb;

[SupportedOSPlatform("windows")]
public static class NeoHidSwitcher
{
    private const int HidVendorId  = 0x081e;
    private const int HidProductId = 0xbd04;
    private const int ReenumerationWaitMs = 5000;

    public static bool TrySwitchToCommsMode() => TrySwitchToCommsMode(out _);

    /// <summary>
    /// Sends the five mode-switch bytes via WinUSB on BD04, matching neotools'
    /// flip_to_comms_mode() exactly: bmRequestType=0x20 (Class|Device, not Interface),
    /// SET_CONFIGURATION first, single send with a generous wait for BD01 to appear.
    /// </summary>
    public static bool TrySwitchToCommsMode(out string diagnostics)
    {
        string? devicePath = FindWinUsbDevicePath(HidVendorId, HidProductId);
        DbgLog.W($"TrySwitchToCommsMode: WinUSB devicePath={devicePath ?? "(null)"}");
        if (devicePath is null)
        {
            diagnostics = "WinUSB BD04 device not found - run Setup Drivers with Neo connected.";
            return false;
        }

        using SafeFileHandle handle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            nint.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOverlapped,
            nint.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            diagnostics = $"CreateFile on WinUSB BD04 failed (Win32 {err}).";
            return false;
        }

        if (!NativeMethods.WinUsb_Initialize(handle, out nint winUsbHandle))
        {
            int err = Marshal.GetLastWin32Error();
            diagnostics = $"WinUsb_Initialize failed (Win32 {err}).";
            return false;
        }
        DbgLog.W("TrySwitchToCommsMode: WinUsb_Initialize ok");

        try
        {
            // Send once, retry only if the transfer itself fails outright. Do NOT wait
            // here for BD01 to appear/settle - that's CmdSwitch's job (it force-installs
            // BD01's WinUSB driver the moment it's detected). If we wait+timeout here,
            // the caller treats it as total failure and never runs that detection loop,
            // even though BD01 may show up moments later and then get stranded driverless.
            diagnostics = "no attempts made";
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                uint dummy = 0;
                var setCfg = new NativeMethods.WinUsbSetupPacket
                {
                    RequestType = 0x00,  // Standard | Device | Host→Device
                    Request     = 9,     // SET_CONFIGURATION
                    Value       = 1,
                    Index       = 0,
                    Length      = 0
                };
                bool cfgOk = NativeMethods.WinUsb_ControlTransfer(winUsbHandle, setCfg, null, 0, ref dummy, nint.Zero);
                DbgLog.W($"TrySwitchToCommsMode: attempt {attempt} SET_CONFIGURATION ok={cfgOk}");

                bool sent = SendSwitchReports(winUsbHandle, out string sendDiag);
                DbgLog.W($"TrySwitchToCommsMode: attempt {attempt} sent={sent} diag={sendDiag}");
                if (sent)
                {
                    diagnostics = $"Mode-switch bytes sent (attempt {attempt}).";
                    return true;
                }
                diagnostics = $"attempt {attempt}: {sendDiag}";
                // one retry for a genuine send failure (e.g. transient STALL) - falls through to attempt 2
            }
            return false;
        }
        finally
        {
            NativeMethods.WinUsb_Free(winUsbHandle);
        }
    }

    private static bool SendSwitchReports(nint winUsbHandle, out string diagnostics)
    {
        for (byte b = 0xe0; b <= 0xe4; b++)
        {
            var setup = new NativeMethods.WinUsbSetupPacket
            {
                RequestType = 0x20,      // Class | Device | Host→Device - matches neotools exactly
                Request     = 9,         // SET_REPORT
                Value       = 0x0200,
                Index       = 1,
                Length      = 1
            };
            byte[] data = [b];
            uint transferred = 0;
            if (!NativeMethods.WinUsb_ControlTransfer(winUsbHandle, setup, data, 1, ref transferred, nint.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                diagnostics = $"ControlTransfer failed at 0x{b:x2} win32={err}";
                return false;
            }
        }
        diagnostics = "Mode-switch reports sent via WinUSB.";
        return true;
    }

    public static bool WaitForCommsDevice(int timeoutMs = ReenumerationWaitMs)
    {
        int elapsed = 0;
        const int pollMs = 200;
        while (elapsed < timeoutMs)
        {
            if (IsCommsDevicePresent()) return true;
            Thread.Sleep(pollMs);
            elapsed += pollMs;
        }
        return false;
    }

    public static bool IsCommsDevicePresent()
        => NeoUsbTransport.FindDevicePath() is not null;

    // Reuses NeoUsbTransport's path lookup which has a DeviceClasses fallback for
    // when SetupDi doesn't find the interface because Linked is not yet written.
    private static string? FindWinUsbDevicePath(int vendorId, int productId)
        => NeoUsbTransport.FindDevicePathByVidPid(vendorId, productId);
}
