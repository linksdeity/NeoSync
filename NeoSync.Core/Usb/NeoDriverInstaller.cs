// libwdi P/Invoke and driver installation logic for WinUSB on BD04 and BD01.
// libwdi.dll must be present next to the executable (copied from native/).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NeoSync.Core.Usb;

/// <summary>Installs WinUSB for both Neo USB interfaces using libwdi.</summary>
public static class NeoDriverInstaller
{
    private const string LibWdi = "libwdi.dll";

    // wdi_driver_type
    private const int WDI_WINUSB = 0;

    // wdi_error
    public const int WDI_SUCCESS           =   0;
    public const int WDI_ERROR_OTHER        = -99;
    public const int WDI_ERROR_NEEDS_ADMIN  = -15;

    [StructLayout(LayoutKind.Sequential)]
    private struct WdiDeviceInfo
    {
        public nint next;           // struct wdi_device_info*
        public ushort vid;
        public ushort pid;
        public int is_composite;    // BOOL
        public byte mi;
        public nint desc;           // char*
        public nint driver;         // char*
        public nint device_id;      // char*
        public nint hardware_id;    // char*
        public nint compatible_id;  // char*
        public nint upper_filter;   // char*
        public ulong driver_version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WdiOptionsPrepareDriver
    {
        public int driver_type;
        public nint vendor_name;    // char*
        public nint device_guid;    // char*
        public int disable_cat;
        public int disable_signing;
        public nint cert_subject;   // char*
        public int use_wcid_driver;
        public int external_inf;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WdiOptionsInstallDriver
    {
        public nint hWnd;           // HWND
        public int install_filter_driver;
        public uint pending_install_timeout;
    }

    [DllImport(LibWdi, CallingConvention = CallingConvention.StdCall)]
    private static extern nint wdi_strerror(int errcode);

    [DllImport(LibWdi, CallingConvention = CallingConvention.StdCall)]
    private static extern int wdi_prepare_driver(
        ref WdiDeviceInfo device_info,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        [MarshalAs(UnmanagedType.LPStr)] string inf_name,
        ref WdiOptionsPrepareDriver options);

    [DllImport(LibWdi, CallingConvention = CallingConvention.StdCall)]
    private static extern int wdi_install_driver(
        ref WdiDeviceInfo device_info,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        [MarshalAs(UnmanagedType.LPStr)] string inf_name,
        ref WdiOptionsInstallDriver options);

    public static string GetErrorString(int code)
    {
        nint ptr = wdi_strerror(code);
        return ptr == 0 ? $"error {code}" : Marshal.PtrToStringAnsi(ptr) ?? $"error {code}";
    }

    /// <summary>
    /// Installs WinUSB for one device (vid+pid). Returns WDI_SUCCESS or an error code.
    /// Uses libwdi to generate a self-signed INF/CAT and install it: wdi_install_driver
    /// adds the self-signed cert to the trusted store, stages the OEM INF, and calls
    /// UpdateDriverForPlugAndPlayDevicesW with INSTALLFLAG_FORCE to override kbdhid on BD04.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static int InstallWinUsb(ushort vid, ushort pid, string description, string workDir, nint parentHwnd = 0)
    {
        string tag = $"InstallWinUsb PID_{pid:X4} ({description})";
        DbgLog.W($"{tag}: start workDir={workDir}");
        Directory.CreateDirectory(workDir);

        var info = new WdiDeviceInfo { vid = vid, pid = pid };
        nint descPtr = Marshal.StringToHGlobalAnsi(description);
        try
        {
            info.desc = descPtr;
            var prepOpts = new WdiOptionsPrepareDriver { driver_type = WDI_WINUSB };
            int rc = wdi_prepare_driver(ref info, workDir, "device.inf", ref prepOpts);
            DbgLog.W($"{tag}: wdi_prepare_driver rc={rc} ({GetErrorString(rc)})");
            if (rc != WDI_SUCCESS)
                return rc;
        }
        finally
        {
            Marshal.FreeHGlobal(descPtr);
            info.desc = 0; // prevent dangling pointer; libwdi already copied it internally
        }

        // hardware_id is required so the installer subprocess calls UpdateDriverForPlugAndPlayDevicesU.
        // Without it the subprocess skips force-install and only stages the OEM INF.
        string hwId = $"USB\\VID_{vid:X4}&PID_{pid:X4}";
        nint hwIdPtr = Marshal.StringToHGlobalAnsi(hwId);
        int rcInst;
        try
        {
            info.hardware_id = hwIdPtr;
            var instOpts = new WdiOptionsInstallDriver { hWnd = parentHwnd };
            DbgLog.W($"{tag}: wdi_install_driver hardware_id={hwId}");
            rcInst = wdi_install_driver(ref info, workDir, "device.inf", ref instOpts);
            DbgLog.W($"{tag}: wdi_install_driver rc={rcInst} ({GetErrorString(rcInst)})");
        }
        finally
        {
            Marshal.FreeHGlobal(hwIdPtr);
            info.hardware_id = 0;
        }

        if (rcInst == WDI_ERROR_OTHER)
        {
            DbgLog.W($"{tag}: WDI_ERROR_OTHER → fallback: DeleteCommittedOemInf then stage-only");
            // Device not connected or in phantom state (e.g. BD01 at install time).
            // Delete the phantom's committed OEM INF so pnputil sets CONFIGFLAG_REINSTALL,
            // forcing Windows to re-run driver matching when the device next connects.
            DeleteCommittedOemInf(vid, pid);

            // Stage WinUSB OEM INF (hardware_id=null → installer falls through to SetupCopyOEMInfU).
            var instOpts = new WdiOptionsInstallDriver { hWnd = parentHwnd };
            rcInst = wdi_install_driver(ref info, workDir, "device.inf", ref instOpts);
            DbgLog.W($"{tag}: stage-only install rc={rcInst} ({GetErrorString(rcInst)})");
        }

        if (rcInst == WDI_SUCCESS)
            System.Threading.Thread.Sleep(3000); // wait for WinUSB to load
        DbgLog.W($"{tag}: → final rc={rcInst}");
        return rcInst;
    }

    // Reads Driver → class key → InfPath for all instances of a VID/PID and removes
    // the OEM INF from the driver store so Windows re-matches on next device plug-in.
    [SupportedOSPlatform("windows")]
    private static void DeleteCommittedOemInf(ushort vid, ushort pid)
    {
        string vidPid = $"VID_{vid:X4}&PID_{pid:X4}";
        DbgLog.W($"DeleteCommittedOemInf {vidPid}");
        using RegistryKey? usbKey =
            Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usbKey is null) { DbgLog.W("  USB key not found"); return; }

        foreach (string devId in usbKey.GetSubKeyNames())
        {
            if (!devId.Contains(vidPid, StringComparison.OrdinalIgnoreCase)) continue;
            using RegistryKey? devKey = usbKey.OpenSubKey(devId);
            if (devKey is null) continue;
            foreach (string instId in devKey.GetSubKeyNames())
            {
                using RegistryKey? instKey = devKey.OpenSubKey(instId);
                if (instKey is null) continue;
                string? driverRef = instKey.GetValue("Driver") as string;
                DbgLog.W($"  inst={instId} Driver={driverRef ?? "(null)"}");
                if (driverRef is null) continue;
                using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\Class\{driverRef}");
                string? oemInf = classKey?.GetValue("InfPath") as string;
                DbgLog.W($"  classKey InfPath={oemInf ?? "(null)"}");
                if (oemInf?.StartsWith("oem", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var (ok, output) = RunProcess("pnputil.exe", $"/delete-driver \"{oemInf}\" /uninstall /force");
                    DbgLog.W($"  pnputil /delete-driver {oemInf} → ok={ok} {output.Replace("\r\n", " ").Trim()}");
                }
            }
        }
    }

    private static (bool ok, string output) RunProcess(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Install WinUSB on both Neo VID/PID pairs (BD04 and BD01).</summary>
    /// <remarks>
    /// Tries HID-based mode switch first (preserves kbdhid = keyboard stays working).
    /// Falls back to installing WinUSB on BD04 only if interface 1 is not accessible via HID.
    /// Total elapsed time is roughly 12-15 seconds.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static (int rcBd04, int rcBd01) InstallBoth(string workDir, nint parentHwnd = 0)
    {
        const ushort Vid = 0x081E;
        const string Bd01InstanceId = @"USB\VID_081E&PID_BD01\5&938d0f6&0&13";

        DbgLog.W($"InstallBoth: log → {DbgLog.FilePath}");

        var preState = NeoDriverState.Detect();
        DbgLog.W($"InstallBoth: pre-install state BD04={preState.Hid} BD01={preState.Comms}");

        // If BD01 is already physically present (Neo already in comms mode or driver error),
        // skip the mode switch entirely and force-install BD01 directly.
        bool bd01AlreadyPresent = preState.Comms == NeoDriverKind.DriverError
                                  || NativeMethods.IsDevicePresent(Bd01InstanceId);
        DbgLog.W($"InstallBoth: bd01AlreadyPresent={bd01AlreadyPresent}");

        int rc04 = WDI_SUCCESS;

        if (!bd01AlreadyPresent)
        {
            // Try HID interface 1 first — no WinUSB on BD04, keyboard stays active.
            bool switched = NeoHidSwitcher.TrySwitchViaHid(out string switchDiag);
            DbgLog.W($"InstallBoth: HID switch: ok={switched} diag={switchDiag}");

            if (!switched)
            {
                // HID interface 1 not accessible; fall back to WinUSB on BD04.
                if (!preState.HidReady)
                {
                    rc04 = InstallWinUsb(Vid, 0xBD04, "AlphaSmart Neo (HID)", Path.Combine(workDir, "bd04"), parentHwnd);
                    DbgLog.W($"InstallBoth: BD04 install rc={rc04}");
                    if (rc04 != WDI_SUCCESS) return (rc04, WDI_SUCCESS);
                }
                else
                {
                    DbgLog.W("InstallBoth: BD04 already WinUSB — skipping install");
                }

                // Retry WinUSB mode switch (device may still be re-enumerating after install).
                switchDiag = "Timed out waiting for BD04 WinUSB device.";
                for (int i = 0; i < 30; i++)
                {
                    switched = NeoHidSwitcher.TrySwitchToCommsMode(out switchDiag);
                    if (switched) break;
                    if (!switchDiag.Contains("not found", StringComparison.OrdinalIgnoreCase)) break;
                    DbgLog.W($"InstallBoth: mode switch attempt {i + 1} — retrying");
                    System.Threading.Thread.Sleep(500);
                }
                DbgLog.W($"InstallBoth: WinUSB switch: ok={switched} diag={switchDiag}");
            }

            DbgLog.W("InstallBoth: sleeping 7000ms for BD01 enumeration");
            System.Threading.Thread.Sleep(7000);
            DbgLog.W("InstallBoth: sleep done");
        }
        else
        {
            DbgLog.W("InstallBoth: BD01 already present — force-installing directly");
        }

        int rc01 = InstallWinUsb(Vid, 0xBD01, "AlphaSmart Neo (Comms)", Path.Combine(workDir, "bd01"), parentHwnd);
        DbgLog.W($"InstallBoth: BD01 install rc={rc01}");

        if (rc01 == WDI_SUCCESS)
        {
            // Poll until WinUSB is actually running on BD01, or retry once if it never starts.
            // A stage-only install (device absent during wdi_install_driver) leaves Comms != WinUsb.
            const int pollMs = 1000;
            const int maxMs  = 15_000;
            int elapsed = 0;
            NeoDriverKind commsKind = NeoDriverState.Detect().Comms;
            while (commsKind != NeoDriverKind.WinUsb && elapsed < maxMs)
            {
                DbgLog.W($"InstallBoth: BD01 state={commsKind}, waiting ({elapsed}ms elapsed)…");
                System.Threading.Thread.Sleep(pollMs);
                elapsed += pollMs;
                commsKind = NeoDriverState.Detect().Comms;
            }

            if (commsKind != NeoDriverKind.WinUsb)
            {
                DbgLog.W("InstallBoth: BD01 not WinUsb after poll — retrying force-install");
                rc01 = InstallWinUsb(Vid, 0xBD01, "AlphaSmart Neo (Comms)", Path.Combine(workDir, "bd01"), parentHwnd);
                DbgLog.W($"InstallBoth: BD01 retry rc={rc01}");
            }
            else
            {
                DbgLog.W($"InstallBoth: BD01 WinUsb confirmed after {elapsed}ms");
            }
        }

        return (rc04, rc01);
    }

    /// <summary>
    /// Removes the WinUSB OEM driver for one device by its published INF name (e.g. oem6.inf).
    /// Runs pnputil /delete-driver which requires elevation; returns true on success.
    /// </summary>
    public static (bool ok, string output) RemoveWinUsb(string infPath)
    {
        if (string.IsNullOrWhiteSpace(infPath))
            return (false, "No INF path provided.");
        return RunProcess("pnputil.exe", $"/delete-driver \"{infPath}\" /uninstall /force");
    }

    /// <summary>
    /// Force-reinstalls WinUSB on BD01 while the device is physically connected in DriverError state.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static int TryFixBd01DriverError(string workDir) =>
        InstallWinUsb(0x081E, 0xBD01, "AlphaSmart Neo (Comms)", Path.Combine(workDir, "bd01"));

    /// <summary>Remove WinUSB from both Neo devices using INF paths from NeoDriverState.</summary>
    /// <remarks>
    /// Scans C:\Windows\INF for every OEM INF referencing each VID/PID and deletes them
    /// all in one pass, preventing Windows from silently reassigning a fallback package.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static (bool ok04, string out04, bool ok01, string out01) RemoveBoth(
        string? hidInfPath, string? commsInfPath)
    {
        var (ok04, out04) = RemoveAllMatchingInfs("VID_081E&PID_BD04");
        var (ok01, out01) = RemoveAllMatchingInfs("VID_081E&PID_BD01");
        return (ok04, out04, ok01, out01);
    }

    [SupportedOSPlatform("windows")]
    private static (bool ok, string output) RemoveAllMatchingInfs(string vidPid)
    {
        string infDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

        var matches = new List<string>();
        foreach (string path in Directory.EnumerateFiles(infDir, "oem*.inf"))
        {
            try
            {
                if (File.ReadAllText(path).Contains(vidPid, StringComparison.OrdinalIgnoreCase))
                    matches.Add(Path.GetFileName(path));
            }
            catch { }
        }

        if (matches.Count == 0)
            return (false, $"No staged INFs found for {vidPid}.");

        var sb = new System.Text.StringBuilder();
        bool anyOk = false;
        foreach (string inf in matches)
        {
            var (ok, output) = RemoveWinUsb(inf);
            sb.AppendLine($"{inf}: {(ok ? "removed" : "failed")} — {output.Trim()}");
            if (ok) anyOk = true;
        }
        return (anyOk, sb.ToString().Trim());
    }
}
