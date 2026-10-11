using NeoSync.Core.Ipc;
using NeoSync.Core.Usb;

namespace NeoSync.App;

/// <summary>
/// Single entry point for the three driver/service troubleshooting actions that used to be
/// separate Advanced menu items -- shows current BD04/BD01 driver state and NeoSyncHelper
/// service health up front so the right action is obvious, and refreshes that status after
/// each action instead of leaving the user to guess whether it worked.
/// </summary>
internal sealed class RepairNeoSyncForm : Form
{
    private FlowLayoutPanel pnlStatus = null!;

    public RepairNeoSyncForm()
    {
        Text            = "Repair NeoSync";
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;
        MinimumSize     = new Size(460, 0);
        StartPosition   = FormStartPosition.CenterParent;
        Font            = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lblHeading = new Label
        {
            Text     = "Repair NeoSync",
            Font     = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = true,
            Margin   = new Padding(0, 0, 0, 8)
        };

        pnlStatus = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoSize      = true,
            Margin        = new Padding(0, 0, 0, 12)
        };

        var pnlButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            Anchor        = AnchorStyles.Right
        };

        var btnClose = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(6, 0, 0, 0) };
        var btnRepairService = new Button { Text = "Repair NeoSyncHelper service\u2026", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        var btnRemove    = new Button { Text = "Remove WinUSB drivers\u2026",    AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        var btnReinstall = new Button { Text = "Reinstall WinUSB drivers\u2026", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };

        btnReinstall.Click     += (_, _) => DriverInstallFlow.Run(this, RefreshStatus);
        btnRemove.Click        += (_, _) => { RemoveDrivers(); RefreshStatus(); };
        btnRepairService.Click += (_, _) => { ServiceRepair.Run(this); RefreshStatus(); };

        pnlButtons.Controls.AddRange(new Control[] { btnClose, btnRepairService, btnRemove, btnReinstall });

        layout.Controls.Add(lblHeading,  0, 0);
        layout.Controls.Add(pnlStatus,   0, 1);
        layout.Controls.Add(pnlButtons,  0, 2);
        Controls.Add(layout);

        AcceptButton = btnClose;
        CancelButton = btnClose;

        RefreshStatus();
    }

    /// <summary>Re-detects driver state and re-probes the service, refreshing the status
    /// panel in place -- called after every action so it reflects what just happened.</summary>
    private void RefreshStatus()
    {
        var state = NeoDriverState.Detect();
        bool serviceOk = !NeoSyncHelperClient.Send("GET_DIAGNOSTICS").StartsWith("ERROR:");

        pnlStatus.SuspendLayout();
        pnlStatus.Controls.Clear();
        pnlStatus.Controls.Add(DriverSetupForm.MakeStatusLabel("BD04 (mode-switch driver)", state.Hid));
        pnlStatus.Controls.Add(DriverSetupForm.MakeStatusLabel("BD01 (transfer mode driver)", state.Comms));
        pnlStatus.Controls.Add(new Label
        {
            Text      = $"  {(serviceOk ? "\u2713" : "\u2717")}  NeoSyncHelper service: {(serviceOk ? "running" : "not responding")}",
            AutoSize  = true,
            ForeColor = serviceOk ? Color.DarkGreen : Color.Firebrick,
            Margin    = new Padding(0, 2, 0, 2)
        });
        pnlStatus.ResumeLayout();
    }

    private void RemoveDrivers()
    {
        var before = NeoDriverState.Detect();
        if (before.HidInfPath is null && before.CommsInfPath is null)
        {
            MessageBox.Show(this, "No WinUSB drivers were found for the Neo.",
                "NeoSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            "This will remove the WinUSB drivers for the AlphaSmart Neo.\r\n" +
            "You will need to reinstall them before NeoSync can connect again.\r\n\r\nContinue?",
            "Remove WinUSB Drivers", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        Cursor = Cursors.WaitCursor;
        string removeResult = NeoSyncHelperClient.Send("REMOVE_DRIVERS");
        Cursor = Cursors.Default;

        // Response is "OK:<bd04 0/1>:<bd01 0/1>" -- pnputil's own real per-device result,
        // which is reliable regardless of whether the device is currently connected. A
        // live registry re-check was tried here before and reverted: BD01 in particular
        // usually isn't physically present at this point (it only exists after a mode
        // switch, not just from being plugged in), so its driver-state registry entry can
        // be stale, wrongly suggesting removal failed when it actually succeeded.
        string[] parts = removeResult.Split(':');
        if (parts.Length != 3 || parts[0] != "OK")
        {
            MessageBox.Show(this, $"Remove failed: {removeResult}", "NeoSync - Remove Drivers",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        bool ok04 = before.HidInfPath   is null || parts[1] == "1";
        bool ok01 = before.CommsInfPath is null || parts[2] == "1";

        string msg = "";
        if (before.HidInfPath   != null) msg += $"BD04: {(ok04 ? "removed" : "failed -- try again, or unplug and replug the Neo first")}\r\n";
        if (before.CommsInfPath != null) msg += $"BD01: {(ok01 ? "removed" : "failed -- try again, or unplug and replug the Neo first")}";

        // BD04 keeps using its old WinUSB binding until Windows re-enumerates the device --
        // only relevant if the Neo is actually plugged in right now; if it's not, the next
        // time it's connected Windows will bind the default keyboard driver on its own.
        if (before.HidInfPath != null && ok04)
        {
            string? bd04Id = NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD04");
            bool bd04Connected = bd04Id != null && NeoUsbDiag.GetDevicePresence(bd04Id).Present;
            if (bd04Connected)
                msg += "\r\n\r\nUnplug and reconnect the Neo so Windows' default keyboard driver can take over.";
        }

        MessageBox.Show(this, msg.Trim(), "NeoSync - Remove Drivers",
            MessageBoxButtons.OK, ok04 && ok01 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }
}
