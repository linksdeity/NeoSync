using NeoSync.Core.Ipc;

namespace NeoSync.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            // Headless mode called by the Inno Setup installer/uninstaller (already elevated).
            // Routes through NeoSyncHelper (the same service used at runtime) so install-time
            // setup gets the same reliability fixes (auto-restart on transient driver failures,
            // no harmful install/uninstall churn) as normal Connect/Disconnect - previously this
            // duplicated an older, less resilient direct-call implementation.
            if (args[0].Equals("--set-diagnostics", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
            {
                string result = NeoSyncHelperClient.Send($"SET_DIAGNOSTICS {args[1]}");
                Environment.Exit(result == "OK" ? 0 : 1);
                return;
            }
            // Runs the same direct install flow used by Advanced > Reinstall WinUSB drivers in
            // the running app -- the installer launches this instead of re-implementing the
            // staging/replug/mode-switch steps itself, so there's only one real implementation.
            if (args[0].Equals("--driver-setup", StringComparison.OrdinalIgnoreCase))
            {
                ApplicationConfiguration.Initialize();
                DriverInstallFlow.Run(null);
                return;
            }
            // Launched elevated (via Verb="runas") by Advanced > Repair NeoSyncHelper Service -
            // the main app never runs elevated itself, so the actual sc.exe work happens here.
            if (args[0].Equals("--repair-service", StringComparison.OrdinalIgnoreCase))
            {
                Environment.Exit(ServiceRepair.RunElevated() ? 0 : 1);
                return;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}