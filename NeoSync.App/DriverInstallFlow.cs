using NeoSync.Core.Ipc;
using NeoSync.Core.Usb;

namespace NeoSync.App;

/// <summary>
/// The actual "stage drivers, wait for the Neo to be plugged in, switch modes" sequence,
/// used directly by Advanced &gt; Reinstall WinUSB drivers and by the installer. Deliberately
/// skips DriverSetupForm's explanatory shell -- that's reserved for the auto-triggered
/// "drivers aren't ready yet" case, where showing what's wrong first actually matters.
/// </summary>
internal static class DriverInstallFlow
{
    /// <param name="owner">The window to disable/busy-cursor while working, or null when
    /// run headlessly (e.g. from the installer) with no app window to protect.</param>
    /// <param name="onComplete">Invoked on the UI thread once the whole sequence finishes,
    /// win or lose, so a caller showing its own status (DriverSetupForm) can refresh it.</param>
    public static void Run(Form? owner, Action? onComplete = null)
    {
        string driverDir = Path.Combine(AppContext.BaseDirectory, "driver");
        if (!File.Exists(Path.Combine(driverDir, "neosync_bd01.inf")) ||
            !File.Exists(Path.Combine(driverDir, "neosync.cer")))
        {
            MessageBox.Show(owner, $"Driver files missing from:\r\n{driverDir}\r\n\r\nPlease reinstall NeoSync.",
                "NeoSync - Driver Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var dlg = new DriverProgressDialog();
        _ = dlg.Handle; // force handle creation now so Invoke works the instant the worker starts

        Task.Run(() => RunSequence(dlg, driverDir, owner, onComplete));

        SetBusy(owner, true);
        dlg.ShowDialog(owner);
        SetBusy(owner, false);
    }

    private static void RunSequence(DriverProgressDialog dlg, string driverDir, Form? owner, Action? onComplete)
    {
        try
        {
            dlg.SetStatus("Staging drivers…", SystemColors.ControlText);

            // Clear any existing staged package first, so Reinstall always starts from the
            // same clean slate as a machine that's never had NeoSync installed.
            NeoSyncHelperClient.Send("REMOVE_DRIVERS");
            string setupResult = NeoSyncHelperClient.Send($"SETUP_DRIVERS {driverDir}");
            dlg.RefreshDriverStatus();
            RaiseOnComplete(owner, onComplete);

            if (!setupResult.StartsWith("OK"))
            {
                dlg.SetStatus($"Driver staging failed: {setupResult}", Color.Firebrick);
                dlg.SetFinished();
                return;
            }

            // BD04's actual driver doesn't matter here -- the mode switch below force-fixes
            // whatever driver it has as part of doing the switch itself. All that's needed
            // first is for the device to be physically present at all; the service itself
            // then waits for its driver state to actually settle before touching it.
            if (!IsBd04Present())
            {
                dlg.SetStatus("Plug in the Neo to continue…", Color.DarkOrange);
                while (!IsBd04Present())
                {
                    if (dlg.Cancelled) { dlg.CloseSafe(); return; }
                    Thread.Sleep(300);
                }
                dlg.RefreshDriverStatus();
            }

            if (dlg.Cancelled) { dlg.CloseSafe(); return; }
            dlg.SetStatus("Switching to transfer mode… (can take up to 30 seconds)", SystemColors.ControlText);

            // Handles BD04's driver, the mode-switch control transfers, BD01's driver, and
            // waiting for BD01 to enumerate -- all inside this one service call.
            string switchResult = NeoSyncHelperClient.Send("SWITCH");
            dlg.RefreshDriverStatus();

            if (switchResult != "OK")
            {
                // The service's message assumes the main app's Connect button, which doesn't
                // exist in this flow -- strip that guidance and give advice that fits here instead.
                string reason = switchResult.StartsWith("FAIL:") ? switchResult[5..] : switchResult;
                int cutAt = reason.IndexOf(" - click Connect again", StringComparison.Ordinal);
                if (cutAt >= 0) reason = reason[..cutAt];
                dlg.SetStatus($"Mode switch failed: {reason}.\n\nIf this keeps happening, unplug and replug the Neo, then try Reinstall again.", Color.Firebrick);
                dlg.SetFinished();
                return;
            }

            dlg.SetStatus("Driver setup complete! Ready to use.", Color.DarkGreen);
            dlg.SetFinished();
            Thread.Sleep(1200); // let the success message actually be seen before moving on
            dlg.CloseSafe();
        }
        catch (Exception ex)
        {
            dlg.SetStatus($"Driver setup step failed: {ex.Message}", Color.Firebrick);
            dlg.SetFinished();
        }
        finally
        {
            RaiseOnComplete(owner, onComplete);
        }
    }

    private static bool IsBd04Present()
        => NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD04") is { } id
        && NeoUsbDiag.GetDevicePresence(id).Present;

    private static void RaiseOnComplete(Form? owner, Action? onComplete)
    {
        if (onComplete is null) return;
        if (owner is not null) owner.Invoke(onComplete);
        else onComplete();
    }

    private static void SetBusy(Form? owner, bool busy)
    {
        if (owner is null) return;
        owner.Cursor  = busy ? Cursors.WaitCursor : Cursors.Default;
        owner.Enabled = !busy;
    }
}

