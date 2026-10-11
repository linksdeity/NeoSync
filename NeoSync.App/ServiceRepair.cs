// Repairs the NeoSyncHelper Windows service registration when it's missing or wedged
// (e.g. an external tool removed it, or a Smart App Control block on the installer
// prevented a reinstall from completing) without requiring a full app reinstall.
using System.ComponentModel;
using System.Diagnostics;
using NeoSync.Core.Ipc;

namespace NeoSync.App;

internal static class ServiceRepair
{
    private const string HelperExeName = "NeoSyncHelper.exe";

    /// <summary>UI-side entry point (Advanced menu). Checks whether the service is actually
    /// unresponsive first, then relaunches this same exe elevated to do the real repair --
    /// the main app intentionally never runs elevated itself.</summary>
    public static void Run(Form owner)
    {
        if (!IsUnresponsive())
        {
            MessageBox.Show(owner, "NeoSyncHelper service is already running fine.",
                "NeoSync - Repair Service", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(owner,
            "The NeoSyncHelper service isn't responding. NeoSync can reinstall its Windows " +
            "service registration to fix this - Windows will prompt for administrator approval." +
            "\r\n\r\nContinue?",
            "NeoSync - Repair Service", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        owner.Cursor = Cursors.WaitCursor;
        try
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath, "--repair-service")
            {
                UseShellExecute = true,
                Verb            = "runas"
            };
            using var proc = Process.Start(psi);
            proc!.WaitForExit();
        }
        catch (Win32Exception)
        {
            // User clicked "No" on the UAC prompt, or elevation was otherwise refused.
            owner.Cursor = Cursors.Default;
            MessageBox.Show(owner, "Repair was cancelled - administrator approval is required.",
                "NeoSync - Repair Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        owner.Cursor = Cursors.Default;

        if (!IsUnresponsive())
        {
            MessageBox.Show(owner, "NeoSyncHelper service is back up and responding.",
                "NeoSync - Repair Service", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(owner,
                "The service still isn't responding. It may be blocked by security software " +
                "(e.g. Windows Smart App Control) - check Windows Security for a blocked-app " +
                "notification, or try reinstalling NeoSync.",
                "NeoSync - Repair Service", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool IsUnresponsive() => NeoSyncHelperClient.Send("GET_DIAGNOSTICS").StartsWith("ERROR:");

    /// <summary>Does the actual reinstall - only ever called from the elevated
    /// --repair-service process (see Program.cs), never directly from the UI. Mirrors the
    /// installer's own stop/delete/create/start sequence (NeoSync.iss) so this produces the
    /// exact same service registration a fresh install would.</summary>
    public static bool RunElevated()
    {
        string helperPath = Path.Combine(AppContext.BaseDirectory, HelperExeName);
        if (!File.Exists(helperPath)) return false;

        RunSc("stop NeoSyncHelper");
        RunSc("delete NeoSyncHelper");
        if (RunSc($"create NeoSyncHelper binPath= \"{helperPath}\" start= auto type= own displayname= \"NeoSync Helper\"") != 0)
            return false;
        return RunSc("start NeoSyncHelper") == 0;
    }

    private static int RunSc(string args)
    {
        var psi = new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
