using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeoSync.Core.Usb;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NeoSync.Service;

internal sealed class NeoSyncWorker : BackgroundService
{
    private readonly ILogger<NeoSyncWorker> _logger;

    public NeoSyncWorker(ILogger<NeoSyncWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NeoSyncHelper started");
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipeServer(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create pipe server");
                await Task.Delay(5_000, stoppingToken);
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WaitForConnection failed");
                pipe.Dispose();
                await Task.Delay(1_000, stoppingToken);
                continue;
            }
            // Handle each connection on a thread-pool thread so the accept loop keeps running.
            _ = Task.Run(() => HandleConnection(pipe), CancellationToken.None);
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        var ps = new PipeSecurity();
        // Allow all authenticated local users to read/write the pipe.
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            "NeoSyncHelper", PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            0, 0, ps);
    }

    private void HandleConnection(NamedPipeServerStream pipe)
    {
        try
        {
            using (pipe)
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                string? cmd = reader.ReadLine();
                if (cmd is null) return;
                _logger.LogInformation("\u2190 {cmd}", cmd);
                string resp = Dispatch(cmd);
                _logger.LogInformation("\u2192 {resp}", resp);
                writer.WriteLine(resp);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "HandleConnection error"); }
    }

    private string Dispatch(string command)
    {
        try
        {
            if (command == "SWITCH")                          return CmdSwitch();
            if (command.StartsWith("RESTART_BD01 "))          return CmdRestartBd01(command[13..]);
            if (command == "FORCE_BD01")                      return CmdForceBd01();
            if (command.StartsWith("SETUP_DRIVERS "))         return CmdSetupDrivers(command[14..]);
            if (command == "REMOVE_DRIVERS")                  return CmdRemoveDrivers();
            return "FAIL:unknown command";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatch error for: {cmd}", command);
            return $"FAIL:{ex.Message}";
        }
    }

    // ── Command handlers ─────────────────────────────────────────────────────

    private string CmdSwitch()
    {
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NeoSync");
        Directory.CreateDirectory(logDir);
        string log = Path.Combine(logDir, "neosync-debug.log");

        NeoUsbDiag.ClearReinstallFlag("VID_081E&PID_BD04");
        NeoUsbDiag.ClearReinstallFlag("VID_081E&PID_BD01");

        // A retry loop here (reinstall + resend within seconds) proved worse than a single
        // clean attempt in testing - repeated back-to-back WinUSB install/reset cycles can
        // wedge the port entirely. Do one attempt; if it fails, tell the user to physically
        // replug (a real reset) and click Connect again rather than retrying automatically.
        var (bd01Id, diag) = TrySwitchOnce(log);

        if (bd01Id is null) return $"FAIL:{diag} - try unplugging and replugging the Neo, then Connect again";
        NeoUsbDiag.GrantDeviceUserAccess(bd01Id);
        return "OK";
    }

    /// <summary>One full switch cycle: install BD04 WinUSB if needed, send the mode-switch
    /// bytes, then watch for BD01 to appear and force-install its driver. Returns the BD01
    /// instance ID on success, or null with a diagnostic message.</summary>
    private (string? bd01Id, string diag) TrySwitchOnce(string log)
    {
        var state = NeoDriverState.Detect();
        _logger.LogWarning("SWITCH: BD04={bd04} BD01={bd01}", state.Hid, state.Comms);
        File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss}] SWITCH start BD04={state.Hid} BD01={state.Comms}\n");

        // Fast path: BD01 may already be fully switched and ready (e.g. from a previous
        // Connect this session) - BD04 may not even physically exist anymore at this point,
        // so skip straight past any BD04 install attempt (which would otherwise burn a full
        // 30s WaitForWinUsb timeout looking for a device that's no longer there).
        if (state.Comms == NeoDriverKind.WinUsb)
        {
            string? existingBd01 = NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD01");
            if (existingBd01 is not null && NeoUsbDiag.GetDevicePresence(existingBd01).Present)
            {
                _logger.LogWarning("SWITCH: BD01 already present and ready - skipping switch");
                return (existingBd01, "");
            }
        }

        // BD01 can already be switched (e.g. the installer's own driver-setup step just did
        // it) but land in transient CM_PROB_FAILED_START before the app ever connects - that
        // shows up here as Comms=DriverError, not WinUsb, so the fast path above misses it and
        // would otherwise fall through into a doomed BD04 reinstall attempt (BD04 no longer
        // physically exists once the Neo has already switched). Self-heal it directly instead.
        if (state.Comms == NeoDriverKind.DriverError)
        {
            string? bd01Instance = NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD01");
            if (bd01Instance is not null)
            {
                for (int attempt = 1; attempt <= 2 && state.Comms == NeoDriverKind.DriverError; attempt++)
                {
                    _logger.LogWarning("SWITCH: BD01 already switched but in error state - restarting device (attempt {n})", attempt);
                    Bd04WinUsbCycle.RestartDevice(bd01Instance);
                    for (int i = 0; i < 60; i++) // up to ~12s
                    {
                        Thread.Sleep(200);
                        state = NeoDriverState.Detect();
                        if (state.Comms != NeoDriverKind.DriverError) break;
                    }
                }
                _logger.LogWarning("SWITCH: BD01 after restart={bd01}", state.Comms);

                if (state.Comms == NeoDriverKind.WinUsb && NeoUsbDiag.GetDevicePresence(bd01Instance).Present)
                    return (bd01Instance, "");

                return (null, "BD01 was already switched but failed to start - unplug and replug the Neo, then try Connect again");
            }
        }

        // BD04 sometimes lands in transient CM_PROB_FAILED_START purely from Windows'
        // own auto-enumeration on plug-in, before we've touched anything - a soft device
        // restart (not a reinstall) reliably clears this most of the time, but can take
        // 10+ seconds to settle and occasionally needs more than one restart call.
        if (state.Hid == NeoDriverKind.DriverError)
        {
            string? bd04Instance = NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD04");
            if (bd04Instance is not null)
            {
                for (int attempt = 1; attempt <= 2 && state.Hid == NeoDriverKind.DriverError; attempt++)
                {
                    _logger.LogWarning("SWITCH: BD04 in error state - restarting device (attempt {n})", attempt);
                    Bd04WinUsbCycle.RestartDevice(bd04Instance);
                    for (int i = 0; i < 60; i++) // up to ~12s
                    {
                        Thread.Sleep(200);
                        state = NeoDriverState.Detect();
                        if (state.Hid != NeoDriverKind.DriverError) break;
                    }
                }
                _logger.LogWarning("SWITCH: BD04 after restart={bd04}", state.Hid);
            }
        }

        // BD04 has only one real USB interface - no composite split needed.
        // wIndex=1 in the mode-switch request is a firmware-level tag, not a real interface number.
        // Composite (usbccgp) doesn't expose a WinUSB handle, so treat it like "needs install" too.
        if (state.Hid != NeoDriverKind.WinUsb)
        {
            string inf = Path.Combine(AppContext.BaseDirectory, "driver", "neosync_bd04.inf");
            if (!File.Exists(inf)) return (null, "neosync_bd04.inf not found in service directory");
            if (state.Hid == NeoDriverKind.Composite)
            {
                // A stale composite driver in the store can keep outranking a fresh WinUSB
                // force-install even after UpdateDriverForPlugAndPlayDevicesW "succeeds" -
                // purge it so the WinUSB install below actually takes effect.
                Bd04WinUsbCycle.RemoveStagedByOriginalName("neosync_bd04_composite.inf");
            }
            string? bd04Oem = Bd04WinUsbCycle.Install(inf);
            _logger.LogWarning("SWITCH: Install bd04Oem={oem}", bd04Oem ?? "(null)");
            if (bd04Oem is null)
            {
                return (null, "BD04 WinUSB install failed - check that the Neo is connected");
            }
            bool winUsbReady = Bd04WinUsbCycle.WaitForWinUsb(30_000);
            _logger.LogWarning("SWITCH: WaitForWinUsb={ready}", winUsbReady);
            if (!winUsbReady)
            {
                // Do NOT uninstall here: repeated install/uninstall cycles have been observed
                // to progressively wedge this device's Windows PnP state until only a reboot
                // fixes it. Leave the driver staged; a physical replug is the more reliable
                // recovery than another install/remove cycle.
                return (null, "BD04 did not re-enumerate with WinUSB - unplug and replug the Neo, then try Connect again");
            }
        }

        bool switched = NeoHidSwitcher.TrySwitchToCommsMode(out string diag);
        _logger.LogWarning("SWITCH: attempt switched={ok} diag={diag}", switched, diag);
        File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss}] attempt={switched} - {diag}\n");

        if (!switched)
        {
            return (null, $"mode switch failed - {diag}");
        }

        // Don't touch BD04's driver - the Neo may still be physically resetting into BD01,
        // and removing/uninstalling a driver here can trigger a port-level PnP event that
        // interrupts that in-progress reset before BD01 has a chance to settle. We also no
        // longer restore BD04 to its inbox HID driver automatically after success: repeated
        // install/uninstall cycles progressively degrade this device's Windows PnP state.

        // BD01 PnP auto-load is unreliable; force-install the moment BD01 appears physically.
        // The freshly-installed WinUSB driver sometimes lands in CM_PROB_FAILED_START
        // transiently - a soft device restart (not a reinstall) reliably clears this, but
        // can take 10+ seconds to fully settle, so give it generous room here rather than
        // falling through to the app's own (slower) fallback polling.
        string bd01Inf = Path.Combine(AppContext.BaseDirectory, "driver", "neosync_bd01.inf");
        string? bd01Id = null;
        bool forceInstalled = false;
        bool restarted = false;
        for (int i = 0; i < 150 && bd01Id is null; i++) // up to ~30s total
        {
            Thread.Sleep(200);
            string? candidate = NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD01");
            if (candidate is null) continue;

            var (present, _) = NeoUsbDiag.GetDevicePresence(candidate);
            if (!forceInstalled)
            {
                forceInstalled = true;
                _logger.LogWarning("SWITCH: BD01 present - force-installing WinUSB");
                if (File.Exists(bd01Inf))
                    Bd04WinUsbCycle.ForceInstall("USB\\VID_081E&PID_BD01", bd01Inf);
                Thread.Sleep(500);
            }
            else if (present)
            {
                bd01Id = candidate;
            }
            else if (!restarted && i > 10) // ~2s after force-install with still no presence
            {
                restarted = true;
                _logger.LogWarning("SWITCH: BD01 still not ready - restarting device");
                Bd04WinUsbCycle.RestartDevice(candidate);
                Thread.Sleep(2_000);
            }
        }
        _logger.LogWarning("SWITCH: bd01Id={id}", bd01Id ?? "(null - BD01 did not appear)");

        return (bd01Id, bd01Id is null ? "BD01 did not appear after mode switch" : "");
    }

    private static string CmdRestartBd01(string instanceId)
    {
        Bd04WinUsbCycle.RestartDevice(instanceId.Trim());
        return "OK";
    }

    private static string CmdForceBd01()
    {
        string inf = Path.Combine(AppContext.BaseDirectory, "driver", "neosync_bd01.inf");
        if (!File.Exists(inf)) return "FAIL:neosync_bd01.inf not found";
        Bd04WinUsbCycle.ForceInstall("USB\\VID_081E&PID_BD01", inf);
        return "OK";
    }

    private static string CmdSetupDrivers(string driverDir)
    {
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NeoSync");
        Directory.CreateDirectory(logDir);
        string log = Path.Combine(logDir, "neosync-debug.log");

        driverDir = driverDir.Trim();
        string cer     = Path.Combine(driverDir, "neosync.cer");
        string bd01Inf = Path.Combine(driverDir, "neosync_bd01.inf");
        string bd04Inf = Path.Combine(driverDir, "neosync_bd04.inf");
        foreach (var (label, path) in new (string, string)[]
            { ("neosync.cer", cer), ("neosync_bd01.inf", bd01Inf), ("neosync_bd04.inf", bd04Inf) })
        {
            if (!File.Exists(path)) return $"FAIL:{label} not found: {path}";
        }

        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        RunProcess(Path.Combine(sys, "certutil.exe"), $"-f -addstore Root \"{cer}\"");
        RunProcess(Path.Combine(sys, "certutil.exe"), $"-f -addstore TrustedPublisher \"{cer}\"");

        // Defensive cleanup: an earlier build briefly shipped a composite (usbccgp) driver
        // for BD04 that's no longer used. If it's still in the driver store on this machine
        // (dev testing or a stale install), Windows can keep re-selecting it over WinUSB even
        // after a forced driver update, so always purge it before installing anything else.
        Bd04WinUsbCycle.RemoveStagedByOriginalName("neosync_bd04_composite.inf");

        // BD01: stage only - WinUSB is applied on first connection via ForceInstall in CmdSwitch.
        Bd04WinUsbCycle.RemoveStagedByOriginalName("neosync_bd01.inf");
        RunProcess(Path.Combine(sys, "pnputil.exe"), $"/add-driver \"{bd01Inf}\"");

        // BD04: stage only - CmdSwitch installs/removes WinUSB around each mode switch.
        // If the Neo is currently stuck on a bad driver (e.g. usbccgp from earlier testing),
        // force-replace it now so it comes back as a clean keyboard on next replug.
        Bd04WinUsbCycle.RemoveStagedByOriginalName("neosync_bd04.inf");
        RunProcess(Path.Combine(sys, "pnputil.exe"), $"/add-driver \"{bd04Inf}\"");
        var bd04State = NeoDriverState.Detect();
        File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss}] SETUP_DRIVERS: BD04={bd04State.Hid}\n");
        if (bd04State.Hid is NeoDriverKind.Composite or NeoDriverKind.DriverError or NeoDriverKind.Other)
        {
            Bd04WinUsbCycle.ForceInstall("USB\\VID_081E&PID_BD04", bd04Inf);
            Bd04WinUsbCycle.WaitForWinUsb(15_000);
            Bd04WinUsbCycle.RemoveAny(); // restore inbox kbdhid so the Neo behaves normally until Connect
        }
        return "OK";
    }

    private static string CmdRemoveDrivers()
    {
        NeoDriverInstaller.RemoveBoth(null, null);
        return "OK";
    }

    private static void RunProcess(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            { CreateNoWindow = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }
}
