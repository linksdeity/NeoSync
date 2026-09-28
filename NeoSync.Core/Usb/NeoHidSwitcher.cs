// Switches the AlphaSmart Neo from HID keyboard mode (PID=0xBD04) to
// bulk comms mode (PID=0xBD01) by sending five USB control transfers.
//
// Protocol confirmed against neotools (Linux, C:\repos\neotools\neotools\device.py
// flip_to_comms_mode()): bmRequestType=0x20 (Class|Device - NOT Interface),
// bRequest=9 (SET_REPORT), wValue=0x0200, wIndex=1, one byte per transfer
// (0xE0..0xE4), preceded by SET_CONFIGURATION. BD04 has only one real USB
// interface - there is no MI_01 to split out via a composite driver.
//
// TrySwitchViaHid()/FindHidInterface1Path() below are dead ends kept only for
// the libwdi fallback install path (NeoDriverInstaller.InstallBoth) - they
// will always fail to find MI_01 since it doesn't exist on this device.
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

    // ── HID-direct mode switch (preserves kbdhid on interface 0) ────────────

    /// <summary>
    /// Sends the five mode-switch bytes via HID interface 1 using HidD_SetOutputReport.
    /// Does not require WinUSB on BD04; the keyboard driver stays active.
    /// Returns false if interface 1 is not accessible or has no output reports.
    /// </summary>
    public static bool TrySwitchViaHid(out string diagnostics)
    {
        string? path = FindHidInterface1Path();
        DbgLog.W($"TrySwitchViaHid: MI_01 path={path ?? "(null)"}");
        if (path is null)
        {
            diagnostics = "HID interface 1 (MI_01) not found for BD04.";
            return false;
        }

        using SafeFileHandle handle = NativeMethods.CreateFile(
            path,
            NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            nint.Zero,
            NativeMethods.OpenExisting,
            0,
            nint.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            DbgLog.W($"TrySwitchViaHid: CreateFile failed Win32={err}");
            diagnostics = $"HID MI_01 CreateFile failed (Win32 {err}).";
            return false;
        }

        if (!NativeMethods.HidD_GetPreparsedData(handle, out nint preparsed))
        {
            DbgLog.W("TrySwitchViaHid: HidD_GetPreparsedData failed");
            diagnostics = "HidD_GetPreparsedData failed on MI_01.";
            return false;
        }

        try
        {
            NativeMethods.HidP_GetCaps(preparsed, out var caps);
            DbgLog.W($"TrySwitchViaHid: caps Usage={caps.Usage:x4} UsagePage={caps.UsagePage:x4} OutputReportByteLength={caps.OutputReportByteLength}");
            // Attempt the switch regardless of OutputReportByteLength - if bInterfaceNumber=1
            // the SET_REPORT control transfer will carry wIndex=1 (the correct target).
            int reportLen = Math.Max((int)caps.OutputReportByteLength, 2); // at minimum: reportID + 1 data byte
            byte[] buf = new byte[reportLen];
            for (byte b = 0xe0; b <= 0xe4; b++)
            {
                buf[0] = 0;
                if (buf.Length > 1) buf[1] = b;
                if (!NativeMethods.HidD_SetOutputReport(handle, buf, (uint)buf.Length))
                {
                    int err = Marshal.GetLastWin32Error();
                    DbgLog.W($"TrySwitchViaHid: SetOutputReport failed byte=0x{b:x2} Win32={err}");
                    diagnostics = $"HidD_SetOutputReport failed at 0x{b:x2} (Win32 {err}).";
                    return false;
                }
            }
        }
        finally
        {
            NativeMethods.HidD_FreePreparsedData(preparsed);
        }

        diagnostics = "Mode-switch via HID MI_01 (keyboard preserved).";
        DbgLog.W("TrySwitchViaHid: success");
        return true;
    }

    /// <summary>
    /// Finds the HID device path for BD04 interface 1 (MI_01 / Col01).
    /// Returns null if the device is absent or only exposes a keyboard collection.
    /// </summary>
    private static string? FindHidInterface1Path()
    {
        NativeMethods.HidD_GetHidGuid(out Guid hidGuid);

        nint devInfo = NativeMethods.SetupDiGetClassDevs(
            ref hidGuid, null, nint.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

        if (devInfo == NativeMethods.InvalidHandleValue) return null;

        string? bestPath     = null;  // mi_01 / col01 preferred; kbd fallback
        string? fallbackPath = null;

        try
        {
            var data = new NativeMethods.SpDeviceInterfaceData();
            data.cbSize = Marshal.SizeOf<NativeMethods.SpDeviceInterfaceData>();

            for (int i = 0; NativeMethods.SetupDiEnumDeviceInterfaces(devInfo, nint.Zero, ref hidGuid, i, ref data); i++)
            {
                string? path = NativeMethods.GetDeviceInterfacePath(devInfo, ref data);
                if (path is null) continue;
                string lower = path.ToLowerInvariant();
                if (!lower.Contains($"vid_{HidVendorId:x4}")) continue;
                DbgLog.W($"FindHidInterface1Path: BD04 candidate: {path}");
                if (lower.Contains("mi_01") || lower.Contains("col01"))
                {
                    bestPath = path;
                    break;
                }
                // Keep any BD04 HID path as a fallback - kbdhid interface may be bInterfaceNumber=1.
                if (lower.Contains($"pid_{HidProductId:x4}"))
                    fallbackPath ??= path;
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(devInfo);
        }

        string? result = bestPath ?? fallbackPath;
        DbgLog.W($"FindHidInterface1Path: result={result ?? "(null)"}");
        return result;
    }
}
