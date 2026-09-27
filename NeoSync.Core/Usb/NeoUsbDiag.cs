// Diagnostic helpers for USB device state: CM_Get_DevNode_Status + ConfigFlags registry reads.
// Used by NeoSync.App to build per-poll-iteration log lines and on-demand diagnostic dumps.
// No EventLog here — NeoSync.Core targets net8.0 (cross-platform); EventLog lives in NeoSync.App.
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace NeoSync.Core.Usb;

[SupportedOSPlatform("windows")]
public static class NeoUsbDiag
{
    /// <summary>Returns (Present, Phantom) for a device instance ID.</summary>
    public static (bool Present, bool Phantom) GetDevicePresence(string instanceId)
        => (NativeMethods.IsDevicePresent(instanceId), NativeMethods.IsDevicePresentOrPhantom(instanceId));

    /// <summary>
    /// Returns (Status, Problem) from CM_Get_DevNode_Status for a device instance ID.
    /// Tries present-only (flags=0) first, then phantom (flags=1) so transitional states are visible.
    /// Returns (0, 0) when the device is not in the PnP tree at all.
    /// </summary>
    public static (uint Status, uint Problem) GetDevNodeStatus(string instanceId)
        => NativeMethods.GetDevNodeStatus(instanceId);

    /// <summary>Finds the currently-present registry instance ID path for the given VID/PID
    /// (exact devId match — a "Contains" match previously also matched composite child
    /// devIds like "...&MI_00"). Prefers a live/present instance over stale ghost ones,
    /// falling back to the first found if none are currently present.</summary>
    public static string? FindVidPidInstanceId(string vidPid)
    {
        try
        {
            using RegistryKey? usbKey = Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey is null) return null;
            string? devId = Array.Find(usbKey.GetSubKeyNames(),
                d => string.Equals(d, vidPid, StringComparison.OrdinalIgnoreCase));
            if (devId is null) return null;
            using RegistryKey? devKey = usbKey.OpenSubKey(devId);
            if (devKey is null) return null;

            string? fallback = null;
            foreach (string instId in devKey.GetSubKeyNames())
            {
                string path = $"USB\\{devId}\\{instId}";
                fallback ??= path;
                if (NativeMethods.IsDevicePresent(path)) return path;
            }
            return fallback;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Reads the ConfigFlags DWORD from the device instance registry key.
    /// CONFIGFLAG_FAILEDINSTALL=0x08, CONFIGFLAG_DISABLED=0x10, CONFIGFLAG_REINSTALL=0x20.
    /// Returns uint.MaxValue if no matching entry is found.
    /// </summary>
    public static uint GetVidPidConfigFlags(string vidPid)
    {
        try
        {
            using RegistryKey? usbKey = Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey is null) return uint.MaxValue;
            foreach (string devId in usbKey.GetSubKeyNames())
            {
                if (!devId.Contains(vidPid, StringComparison.OrdinalIgnoreCase)) continue;
                using RegistryKey? devKey = usbKey.OpenSubKey(devId);
                if (devKey is null) continue;
                foreach (string instId in devKey.GetSubKeyNames())
                {
                    using RegistryKey? instKey = devKey.OpenSubKey(instId);
                    if (instKey is null) continue;
                    object? val = instKey.GetValue("ConfigFlags");
                    return val is int i ? (uint)i : 0;
                }
            }
        }
        catch { }
        return uint.MaxValue;
    }

    /// <summary>
    /// Clears CONFIGFLAG_REINSTALL (0x20) from the BD01 phantom device node before mode switch,
    /// so Windows doesn't trigger a premature REINSTALL driver install when BD01 enumerates.
    /// Uses CM_Set_DevNode_Registry_PropertyW (works even on phantom nodes; bypasses registry ACL).
    /// </summary>
    public static void ClearReinstallFlag(string vidPid)
    {
        string? instanceId = FindVidPidInstanceId(vidPid);
        if (instanceId is null)
        {
            DbgLog.W($"NeoUsbDiag.ClearReinstallFlag: no device found for {vidPid}");
            return;
        }
        uint cfg = GetVidPidConfigFlags(vidPid);
        if ((cfg & 0x20u) == 0)
        {
            DbgLog.W($"NeoUsbDiag.ClearReinstallFlag: {instanceId} cfg=0x{cfg:X} REINSTALL not set, skipping");
            return;
        }
        DbgLog.W($"NeoUsbDiag.ClearReinstallFlag: {instanceId} cfg=0x{cfg:X} → clearing REINSTALL");
        bool ok = NativeMethods.ClearDevNodeConfigFlags(instanceId);
        DbgLog.W($"NeoUsbDiag.ClearReinstallFlag: CM result ok={ok}");
    }

    /// <summary>Grants standard (non-admin) users read/write access to the WinUSB device node.</summary>
    public static bool GrantDeviceUserAccess(string instanceId)
    {
        // D:P = protected DACL; GA = Generic All; BA=Admins, SY=System, BU=Built-in Users
        var sd = new System.Security.AccessControl.RawSecurityDescriptor(
            "D:P(A;;GA;;;BA)(A;;GA;;;SY)(A;;GA;;;BU)");
        byte[] binary = new byte[sd.BinaryLength];
        sd.GetBinaryForm(binary, 0);
        bool ok = NativeMethods.SetDevNodeSecurity(instanceId, binary);
        DbgLog.W($"NeoUsbDiag.GrantDeviceUserAccess: {instanceId} → ok={ok}");
        return ok;
    }

    /// <summary>Formats a (status, problem) pair from CM_Get_DevNode_Status as a compact log token.</summary>
    public static string FormatCmStatus(uint status, uint problem)
    {
        var flags = new StringBuilder();
        if ((status & 0x00000008u) != 0) flags.Append("STARTED|");
        if ((status & 0x00000100u) != 0) flags.Append("DRV_LOADED|");
        if ((status & 0x00000400u) != 0) flags.Append("HAS_PROBLEM|");
        if ((status & 0x01000000u) != 0) flags.Append("NEED_ENUM|");
        string f = flags.Length > 0 ? flags.ToString().TrimEnd('|') : "none";
        string p = problem switch
        {
            0  => "",
            1  => " prob=1(NOT_CONFIGURED)",
            10 => " prob=10(FAILED_START)",
            28 => " prob=28(FAILED_INSTALL)",
            43 => " prob=43(FAILED_POST_START)",
            _  => $" prob={problem}"
        };
        return $"cm=0x{status:X}({f}){p}";
    }

    /// <summary>Formats a ConfigFlags registry DWORD as a compact log token.</summary>
    public static string FormatCfgFlags(uint flags)
    {
        if (flags == uint.MaxValue) return "cfg=notfound";
        var parts = new StringBuilder();
        if ((flags & 0x08u) != 0) parts.Append("FAILEDINSTALL|");
        if ((flags & 0x10u) != 0) parts.Append("DISABLED|");
        if ((flags & 0x20u) != 0) parts.Append("REINSTALL|");
        string f = parts.Length > 0 ? parts.ToString().TrimEnd('|') : "ok";
        return $"cfg=0x{flags:X}({f})";
    }

    /// <summary>Builds a multi-line diagnostic snapshot string (CM state, ConfigFlags, staged INFs).</summary>
    public static string BuildDumpString(NeoDriverState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== NeoSync USB diagnostic dump {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        sb.AppendLine($"Driver state: BD04={state.Hid} ({state.HidInfPath ?? "none"})  BD01={state.Comms} ({state.CommsInfPath ?? "none"})");

        foreach ((string vidPid, string label) in new[] { ("VID_081E&PID_BD04", "BD04"), ("VID_081E&PID_BD01", "BD01") })
        {
            string? instId = FindVidPidInstanceId(vidPid);
            if (instId is not null)
            {
                var (cmStatus, cmProblem) = GetDevNodeStatus(instId);
                uint cfgFlags = GetVidPidConfigFlags(vidPid);
                var (present, phantom) = GetDevicePresence(instId);
                sb.AppendLine($"{label} ({instId}):");
                sb.AppendLine($"  present={present} phantom={phantom}  {FormatCmStatus(cmStatus, cmProblem)}  {FormatCfgFlags(cfgFlags)}");
            }
            else
            {
                sb.AppendLine($"{label}: no registry entry");
            }
        }

        string infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        var oemInfs = Directory.EnumerateFiles(infDir, "oem*.inf")
            .Where(p => { try { return File.ReadAllText(p).Contains("VID_081E", StringComparison.OrdinalIgnoreCase); } catch { return false; } })
            .Select(Path.GetFileName)
            .ToList();
        sb.AppendLine($"Staged OEM INFs: {(oemInfs.Count > 0 ? string.Join(", ", oemInfs) : "none")}");

        return sb.ToString();
    }
}
