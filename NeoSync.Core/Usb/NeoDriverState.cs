// Detects the current Windows driver state for both AlphaSmart Neo USB modes.
// All detection is read-only registry access — no admin required.
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NeoSync.Core.Usb;

/// <summary>What driver is currently bound to a VID/PID pair.</summary>
public enum NeoDriverKind
{
    /// <summary>Device has never been connected; no registry entry exists.</summary>
    NotSeen,
    /// <summary>WinUSB is installed — device is ready for use by NeoSync.</summary>
    WinUsb,
    /// <summary>Windows default HID keyboard driver (BD04 out-of-the-box state).</summary>
    WindowsHid,
    /// <summary>Renaissance Learning / AlphaSmart Manager proprietary driver.</summary>
    RenaissanceLearning,
    /// <summary>WinUSB driver is installed and committed but failed to start (e.g. CM_PROB_FAILED_START).</summary>
    DriverError,
    /// <summary>usbccgp.sys composite parent — creates MI_00/MI_01 child nodes.</summary>
    Composite,
    /// <summary>Some other driver (libusb-win32, libusbK, etc.).</summary>
    Other
}

[SupportedOSPlatform("windows")]
public sealed class NeoDriverState
{
    public NeoDriverKind Hid    { get; }  // BD04 — keyboard mode
    public NeoDriverKind Comms  { get; }  // BD01 — comms/file-transfer mode
    public string? HidInfPath   { get; }  // OEM INF name (e.g. oem6.inf) for BD04, or null
    public string? CommsInfPath { get; }  // OEM INF name for BD01, or null

    // NotSeen means the Neo has never enumerated on this PC; UDPAPD needs the device present.
    public bool HidReady   => Hid != NeoDriverKind.NotSeen;
    public bool CommsReady => Comms == NeoDriverKind.WinUsb;
    public bool BothReady  => HidReady && CommsReady;

    private NeoDriverState(NeoDriverKind hid, NeoDriverKind comms, string? hidInf, string? commsInf)
    {
        Hid          = hid;
        Comms        = comms;
        HidInfPath   = hidInf;
        CommsInfPath = commsInf;
    }

    /// <summary>Reads current driver state from the registry (no admin needed).</summary>
    public static NeoDriverState Detect()
    {
        // Check BD04 (keyboard mode) and BD01 (comms mode).
        var (hid,   hidInf)   = DetectForDevice("VID_081E&PID_BD04");
        var (comms, commsInf) = DetectForDevice("VID_081E&PID_BD01");
        return new(hid, comms, hidInf, commsInf);
    }

    internal static (NeoDriverKind kind, string? infPath) DetectForDevice(string vidPid)
    {
        DbgLog.W($"DetectForDevice {vidPid}");
        using RegistryKey? usbKey =
            Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usbKey is null) { DbgLog.W("  USB key missing → NotSeen"); return (NeoDriverKind.NotSeen, null); }

        // Exact match only — "Contains" previously also matched composite child devIds
        // like "VID_081E&PID_BD04&MI_00", causing their stale/ghost data to be reported
        // for plain "VID_081E&PID_BD04" lookups.
        string? devId = Array.Find(usbKey.GetSubKeyNames(),
            d => string.Equals(d, vidPid, StringComparison.OrdinalIgnoreCase));
        if (devId is null) { DbgLog.W("  no matching devId → NotSeen"); return (NeoDriverKind.NotSeen, null); }

        using RegistryKey? devKey = usbKey.OpenSubKey(devId);
        if (devKey is null) { DbgLog.W("  devId key missing → NotSeen"); return (NeoDriverKind.NotSeen, null); }

        // A device can have multiple instance IDs registered from past connections
        // (different ports, past force-installs, etc.) — only ONE, if any, is the
        // currently-connected live instance. Returning the first instId found regardless
        // of presence was reporting stale ghost-instance data as if it were live.
        (NeoDriverKind kind, string? inf)? fallback = null;
        foreach (string instId in devKey.GetSubKeyNames())
        {
            using RegistryKey? instKey = devKey.OpenSubKey(instId);
            if (instKey is null) continue;

            string? service  = instKey.GetValue("Service")        as string;
            string? provider = instKey.GetValue("DriverProvider")  as string;
            string? inf      = instKey.GetValue("InfPath")         as string;
            DbgLog.W($"  inst={instId} Service={service ?? "(null)"} Provider={provider ?? "(null)"} InfPath={inf ?? "(null)"}");

            // InfPath is often in the driver class key, not the instance key
            if (inf is null)
            {
                string? driverRef = instKey.GetValue("Driver") as string; // e.g. "{guid}\0001"
                DbgLog.W($"  Driver value={driverRef ?? "(null)"}");
                if (driverRef is not null)
                {
                    using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Control\Class\{driverRef}");
                    inf = classKey?.GetValue("InfPath") as string;
                    provider ??= classKey?.GetValue("ProviderName") as string;
                    DbgLog.W($"  classKey InfPath={inf ?? "(null)"} ProviderName={provider ?? "(null)"}");
                }
            }

            string instancePath = $"USB\\{devId}\\{instId}";
            bool present = NativeMethods.IsDevicePresent(instancePath);
            DbgLog.W($"  present={present}");

            (NeoDriverKind kind, string? inf) result;
            uint problem = NativeMethods.GetDevNodeProblem(instancePath);
            if (problem != 0)
            {
                DbgLog.W($"  CM problem code={problem} → DriverError");
                result = (NeoDriverKind.DriverError, inf);
            }
            else
            {
                var kind = ClassifyDriver(service, provider, inf);
                DbgLog.W($"  ClassifyDriver → {kind}");
                result = (kind, inf);
            }

            if (present)
            {
                DbgLog.W($"  using present instance {instId}");
                return result;
            }
            fallback ??= result;
        }

        if (fallback is { } fb)
        {
            DbgLog.W("  no present instance found — falling back to first known instance data");
            return fb;
        }

        DbgLog.W($"  no instances found → NotSeen");
        return (NeoDriverKind.NotSeen, null);
    }

    private static string WindowsInfDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

    private static NeoDriverKind ClassifyDriver(string? service, string? provider, string? inf)
    {
        string svc  = (service  ?? "").ToLowerInvariant();
        string prov = (provider ?? "").ToLowerInvariant();
        string infL = (inf      ?? "").ToLowerInvariant();

        if (svc == "winusb")
            return NeoDriverKind.WinUsb;

        if (svc is "hidusb" or "kbdhid" or "mouhid")
            return NeoDriverKind.WindowsHid;

        if (svc == "usbccgp")
            return NeoDriverKind.Composite;

        // Renaissance Learning / AlphaSmart Manager used various service names;
        // identify by provider string or known INF names
        if (prov.Contains("renaissance") || prov.Contains("alphasmart") ||
            infL.Contains("neo") || infL.Contains("alphasmart"))
            return NeoDriverKind.RenaissanceLearning;

        if (svc.Length > 0 || prov.Length > 0)
            return NeoDriverKind.Other;

        return NeoDriverKind.NotSeen;
    }

    public override string ToString()
        => $"BD04(HID)={Hid}, BD01(Comms)={Comms}";
}
