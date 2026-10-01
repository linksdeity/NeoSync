using NeoSync.Core.Ipc;

namespace NeoSync.App;

/// <summary>
/// Stages the WinUSB driver package (BD01's INF + the signing cert), used directly by
/// Advanced &gt; Reinstall WinUSB drivers and by the installer. Deliberately stops there --
/// the actual mode switch (BD04's WinUSB install, the switch bytes, waiting for BD01) is
/// left entirely to the main app's own Connect button, which already does it reliably on
/// its own; trying to also do it here only added fragile timing-dependent steps for no
/// real benefit. Also skips DriverSetupForm's explanatory shell -- that's reserved for the
/// auto-triggered "drivers aren't ready yet" case, where showing what's wrong first matters.
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

            if (!setupResult.StartsWith("OK"))
            {
                dlg.SetStatus($"Driver staging failed: {setupResult}", Color.Firebrick);
                dlg.SetFinished();
                return;
            }

            dlg.SetStatus("Drivers staged! Plug in the Neo and click Connect in the main window.", Color.DarkGreen);
            dlg.SetFinished();
            Thread.Sleep(1200); // let the message actually be seen before moving on
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

