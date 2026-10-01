using NeoSync.Core.Ipc;
using NeoSync.Core.Usb;

namespace NeoSync.App;

/// <summary>
/// The actual "stage drivers, wait for replug, switch modes" sequence, used directly by
/// Advanced &gt; Reinstall WinUSB drivers and by the installer. Deliberately skips
/// DriverSetupForm's explanatory shell -- that's reserved for the auto-triggered "drivers
/// aren't ready yet" case, where showing what's wrong first actually matters.
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

        string setupResult;
        SetBusy(owner, true);
        try
        {
            // Clear any existing staged package first, so Reinstall always starts from the
            // same clean slate as a machine that's never had NeoSync installed.
            NeoSyncHelperClient.Send("REMOVE_DRIVERS");
            setupResult = NeoSyncHelperClient.Send($"SETUP_DRIVERS {driverDir}");
        }
        finally
        {
            SetBusy(owner, false);
        }
        onComplete?.Invoke();

        if (!setupResult.StartsWith("OK"))
        {
            MessageBox.Show(owner, $"Driver staging failed: {setupResult}",
                "NeoSync - Driver Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (new ReplugStatusDialog().ShowDialog(owner) == DialogResult.Cancel) return;

        SetBusy(owner, true);

        if (owner is null)
        {
            // No UI thread to protect here -- block, same as the installer's own blocking Exec.
            string msg = RunSwitchAndWait();
            MessageBox.Show(msg, "NeoSync - Driver Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            onComplete?.Invoke();
            return;
        }

        // The mode switch and BD01 wait can take up to 30s -- run off the UI thread so a
        // slow or not-quite-ready device can't freeze the app.
        Task.Run(() =>
        {
            string msg = RunSwitchAndWait();
            owner.Invoke(() =>
            {
                SetBusy(owner, false);
                MessageBox.Show(owner, msg, "NeoSync - Driver Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                onComplete?.Invoke();
            });
        });
    }

    private static string RunSwitchAndWait()
    {
        try
        {
            // Mode switch via service (handles BD04 driver install, control transfers, cleanup).
            string switchResult = NeoSyncHelperClient.Send("SWITCH");
            if (switchResult != "OK")
            {
                return "The driver has been staged but the mode switch failed (device may not be ready).\n\n" +
                       "Click Connect in the main window - the driver will be applied automatically.";
            }

            bool bd01Present = false;
            for (int i = 0; i < 150 && !bd01Present; i++)
            {
                bd01Present = NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD01") is not null;
                if (!bd01Present) Thread.Sleep(200);
            }
            return bd01Present
                ? "Driver setup complete. Click Connect in the main window to start."
                : "The switch succeeded but BD01 took too long to enumerate.\n\nClick Connect in the main window.";
        }
        catch (Exception ex)
        {
            return $"Driver setup step failed: {ex.Message}";
        }
    }

    private static void SetBusy(Form? owner, bool busy)
    {
        if (owner is null) return;
        owner.Cursor  = busy ? Cursors.WaitCursor : Cursors.Default;
        owner.Enabled = !busy;
    }
}
