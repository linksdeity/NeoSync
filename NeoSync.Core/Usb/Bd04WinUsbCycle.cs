// Temporarily installs WinUSB on the BD04 device (breaks keyboard) long enough to
// do the mode switch, then removes it so keyboard is restored on next replug.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeoSync.Core.Usb;

[SupportedOSPlatform("windows")]
public static class Bd04WinUsbCycle
{
    private const string Bd04VidPid       = "VID_081E&PID_BD04";
    private const uint   INSTALLFLAG_FORCE          = 0x00000001;
    private const uint   INSTALLFLAG_NONINTERACTIVE  = 0x00000004;

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(
        nint   hwndParent,
        string hardwareId,
        string fullInfPath,
        uint   installFlags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    /// <summary>
    /// Stages neosync_bd04.inf and force-installs it on BD04 (requires admin).
    /// Returns the published oem name for later removal, or null on failure.
    /// The device re-enumerates automatically; call WaitForWinUsb() before switching.
    /// </summary>
    public static string? Install(string bd04InfPath)
    {
        DbgLog.W($"Bd04WinUsbCycle.Install: {bd04InfPath}");
        if (!File.Exists(bd04InfPath))
        {
            DbgLog.W("  INF not found");
            return null;
        }

        // pnputil /add-driver ... /install stages AND force-applies in one step - this
        // reliably replaces the running inbox HidUsb driver. The Win32
        // UpdateDriverForPlugAndPlayDevicesW API was tried first but proved unreliable
        // (staged fine, reported success, but WinUSB never actually finished binding -
        // confirmed by testing pnputil directly against the same INF, which worked
        // immediately every time).
        string output = RunPnputil($"/add-driver \"{bd04InfPath}\" /install");
        DbgLog.W($"  pnputil output: {output.Replace('\n', '|')}");
        string? oemName = ParsePublishedName(output);
        DbgLog.W($"  staged as: {oemName ?? "(unknown)"}");

        bool installed = output.Contains("Driver package installed on device", StringComparison.OrdinalIgnoreCase);
        if (!installed)
        {
            DbgLog.W("  install did not report success");
            if (oemName != null) RunPnputil($"/delete-driver \"{oemName}\"");
            return null;
        }

        return oemName;
    }

    private static string? ParsePublishedName(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("Published Name:", StringComparison.OrdinalIgnoreCase))
                return t["Published Name:".Length..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Removes the previously installed BD04 WinUSB driver from the store.
    /// The device re-enumerates to its inbox HID driver; keyboard works after next replug.
    /// </summary>
    public static void Remove(string oemName)
    {
        DbgLog.W($"Bd04WinUsbCycle.Remove: {oemName}");
        string output = RunPnputil($"/delete-driver \"{oemName}\" /uninstall");
        DbgLog.W($"  pnputil output: {output.Replace('\n', '|')}");
    }

    /// <summary>
    /// Removes all driver store entries whose original INF name matches <paramref name="originalInfName"/>.
    /// Call before re-staging to prevent duplicate oem*.inf entries accumulating from repeated installs.
    /// </summary>
    public static void RemoveStagedByOriginalName(string originalInfName)
    {
        DbgLog.W($"Bd04WinUsbCycle.RemoveStagedByOriginalName: {originalInfName}");
        string output = RunPnputil("/enum-drivers");
        string? currentOem = null;
        foreach (string line in output.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("Published Name:", StringComparison.OrdinalIgnoreCase))
            {
                currentOem = t["Published Name:".Length..].Trim();
                continue;
            }
            if (currentOem != null && t.StartsWith("Original Name:", StringComparison.OrdinalIgnoreCase))
            {
                string orig = t["Original Name:".Length..].Trim();
                if (orig.Equals(originalInfName, StringComparison.OrdinalIgnoreCase))
                {
                    DbgLog.W($"  removing {currentOem}");
                    Remove(currentOem);
                }
                currentOem = null;
            }
            else if (!t.StartsWith("Original Name:", StringComparison.OrdinalIgnoreCase))
            {
                currentOem = null;
            }
        }
    }

    /// <summary>
    /// Removes any BD04 WinUSB driver from the store by searching for our known original name.
    /// Used as a fallback cleanup if the oem name was not persisted.
    /// </summary>
    public static void RemoveAny()
    {
        DbgLog.W("Bd04WinUsbCycle.RemoveAny: scanning driver store");
        string output = RunPnputil("/enum-drivers");
        string? currentOem = null;

        foreach (string line in output.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("Published Name:", StringComparison.OrdinalIgnoreCase))
            {
                currentOem = t["Published Name:".Length..].Trim();
                continue;
            }
            // Match "Original Name:      neosync_bd04.inf" exactly (not the MI01 variant)
            if (currentOem != null &&
                t.StartsWith("Original Name:", StringComparison.OrdinalIgnoreCase) &&
                t.EndsWith("neosync_bd04.inf", StringComparison.OrdinalIgnoreCase))
            {
                DbgLog.W($"  removing {currentOem}");
                Remove(currentOem);
                currentOem = null;
            }
            else if (!t.StartsWith("Original Name:", StringComparison.OrdinalIgnoreCase))
            {
                currentOem = null; // reset if a non-name line appears before we matched
            }
        }
    }

    /// <summary>
    /// Polls until BD04 shows WinUSB as its driver or timeout elapses.
    /// Call after Install() before attempting mode switch.
    /// </summary>
    public static bool WaitForWinUsb(int timeoutMs = 15_000)
    {
        DbgLog.W("Bd04WinUsbCycle.WaitForWinUsb: polling");
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            var state = NeoDriverState.Detect();
            DbgLog.W($"  t={elapsed}ms BD04={state.Hid}");
            if (state.Hid == NeoDriverKind.WinUsb) return true;
            Thread.Sleep(100);
            elapsed += 100;
        }
        DbgLog.W("  timed out");
        return false;
    }

    /// <summary>Force-installs any WinUSB INF onto an arbitrary present device (used for BD01 recovery).</summary>
    public static void ForceInstall(string hardwareId, string infPath)
    {
        DbgLog.W($"Bd04WinUsbCycle.ForceInstall: {hardwareId} {infPath}");
        bool ok = UpdateDriverForPlugAndPlayDevicesW(
            nint.Zero, hardwareId, infPath,
            INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, out _);
        DbgLog.W($"  ForceInstall ok={ok} win32={Marshal.GetLastWin32Error()}");
    }

    public static void RestartDevice(string instanceId)    {
        DbgLog.W($"Bd04WinUsbCycle.RestartDevice: {instanceId}");
        string output = RunPnputil($"/restart-device \"{instanceId}\"");
        DbgLog.W($"  pnputil output: {output.Replace('\n', '|')}");
    }

    /// <summary>
    /// Fully removes a device node (not just a restart), forcing Windows to treat the next
    /// enumeration as brand new hardware and redo driver selection from scratch. A plain
    /// restart has been observed to not be enough to restore the inbox HID keyboard driver
    /// after our WinUSB package is removed - this is the stronger fix for that.
    /// </summary>
    public static void RemoveDeviceNode(string instanceId)
    {
        DbgLog.W($"Bd04WinUsbCycle.RemoveDeviceNode: {instanceId}");
        string output = RunPnputil($"/remove-device \"{instanceId}\" /subtree");
        DbgLog.W($"  pnputil output: {output.Replace('\n', '|')}");

        // A removed device isn't always re-discovered automatically - that depends on the
        // specific USB host controller/hub noticing and re-announcing it, which isn't
        // guaranteed. Explicitly ask Windows to rescan rather than leave that to chance;
        // this is the same operation as Device Manager's "Scan for hardware changes".
        string scanOutput = RunPnputil("/scan-devices");
        DbgLog.W($"  scan-devices output: {scanOutput.Replace('\n', '|')}");
    }

    private static string RunPnputil(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("pnputil.exe", args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (stderr.Length > 0) DbgLog.W($"  RunPnputil stderr: {stderr.Replace('\n', '|')}");
            DbgLog.W($"  RunPnputil exit={p.ExitCode}");
            return output;
        }
        catch (Exception ex)
        {
            DbgLog.W($"  RunPnputil exception: {ex.Message}");
            return "";
        }
    }
}
