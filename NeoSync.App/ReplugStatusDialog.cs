using NeoSync.Core.Usb;

namespace NeoSync.App;

/// <summary>
/// Modal dialog shown while the user unplugs/replugs the Neo so Windows can bind the
/// keyboard driver -- polls and displays the live BD04 driver state so the user doesn't
/// have to guess when it's actually ready.
/// </summary>
internal sealed class ReplugStatusDialog : Form
{
    private readonly System.Windows.Forms.Timer _pollTimer;
    private Label lblState = null!;

    public ReplugStatusDialog()
    {
        Text            = "NeoSync - Driver Setup";
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;
        MinimumSize     = new Size(480, 0);
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

        var lblInstructions = new Label
        {
            AutoSize    = true,
            MaximumSize = new Size(480, 0),
            Text =
                "If the Neo is already plugged in, unplug it now.\r\n" +
                "Then plug it in — this will continue automatically once the status below shows ready.\r\n\r\n" +
                "Click Cancel to skip instead (the driver is staged and will apply when NeoSync connects).",
            Margin = new Padding(0, 0, 0, 8)
        };

        lblState = new Label
        {
            AutoSize = true,
            Font     = new Font("Segoe UI", 9f, FontStyle.Bold),
            Margin   = new Padding(0, 0, 0, 8)
        };

        var pnlButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            Anchor        = AnchorStyles.Right
        };
        var btnCancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        pnlButtons.Controls.Add(btnCancel);

        layout.Controls.Add(lblInstructions, 0, 0);
        layout.Controls.Add(lblState,        0, 1);
        layout.Controls.Add(pnlButtons,      0, 2);
        Controls.Add(layout);

        CancelButton = btnCancel;

        _pollTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _pollTimer.Tick += (_, _) => UpdateState();
        FormClosed += (_, _) => _pollTimer.Dispose();
        // Defer the first check (and the auto-close it can trigger) until the dialog is
        // actually showing -- setting DialogResult before ShowDialog's modal loop starts
        // has no effect, so an already-ready device wouldn't close the dialog at all.
        Shown += (_, _) => { UpdateState(); _pollTimer.Start(); };
    }

    private void UpdateState()
    {
        NeoDriverKind kind = NeoDriverState.Detect().Hid;

        // NeoDriverState falls back to the last-known registry entry for a device that
        // isn't currently present (so the main app can still show useful info about a
        // disconnected Neo) -- but that's exactly wrong here: it previously meant this
        // dialog could think a long-gone Neo was "ready" from stale data alone, closing
        // itself (and letting the mode switch proceed) with nothing actually plugged in.
        string? bd04Id = NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD04");
        bool present = bd04Id is not null && NeoUsbDiag.GetDevicePresence(bd04Id).Present;
        bool ready = present && kind is NeoDriverKind.WinUsb or NeoDriverKind.WindowsHid;

        string desc = !present
            ? "not connected -- plug in the Neo"
            : kind switch
            {
                NeoDriverKind.WindowsHid          => "recognised as a keyboard -- ready!",
                NeoDriverKind.WinUsb              => "recognised (WinUSB) -- ready!",
                NeoDriverKind.RenaissanceLearning => "using the old AlphaSmart Manager driver",
                NeoDriverKind.DriverError         => "driver failed to start",
                NeoDriverKind.NotSeen             => "not seen yet",
                NeoDriverKind.Other               => "driver not yet applied",
                _                                  => kind.ToString()
            };
        lblState.Text      = $"Current state: {desc}";
        lblState.ForeColor = ready ? Color.DarkGreen : Color.Firebrick;
        // Our own live detection is the source of truth now -- no more guessing from
        // USB sounds or the keyboard's light, which don't reliably mean the driver is bound.
        // Setting DialogResult on a shown modal form closes it immediately.
        if (ready) DialogResult = DialogResult.OK;
    }
}
