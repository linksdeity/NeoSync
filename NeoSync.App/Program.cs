using NeoSync.Core.Ipc;

namespace NeoSync.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            // Headless modes called by the Inno Setup installer/uninstaller (already elevated).
            // Both route through NeoSyncHelper (the same service used at runtime) so install-time
            // setup gets the same reliability fixes (auto-restart on transient driver failures,
            // no harmful install/uninstall churn) as normal Connect/Disconnect - previously this
            // duplicated an older, less resilient direct-call implementation.
            if (args[0].Equals("--setup-drivers", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
            {
                string result = NeoSyncHelperClient.Send($"SETUP_DRIVERS {args[1]}");
                Environment.Exit(result == "OK" ? 0 : 1);
                return;
            }
            if (args[0].Equals("--switch-to-comms", StringComparison.OrdinalIgnoreCase))
            {
                string result = NeoSyncHelperClient.Send("SWITCH");
                Environment.Exit(result == "OK" ? 0 : 1);
                return;
            }
            if (args[0].Equals("--set-diagnostics", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
            {
                string result = NeoSyncHelperClient.Send($"SET_DIAGNOSTICS {args[1]}");
                Environment.Exit(result == "OK" ? 0 : 1);
                return;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}