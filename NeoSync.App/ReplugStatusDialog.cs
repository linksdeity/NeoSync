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
                "Then plug it in and wait for the USB connect sound (or the keyboard light).\r\n\r\n" +
                "Click OK once Windows has recognised it as a keyboard, or Cancel to skip\r\n" +
                "(the driver is staged and will apply when NeoSync connects).",
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
        var btnCancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(6, 0, 0, 0) };
        var btnOk     = new Button { Text = "OK",     AutoSize = true, DialogResult = DialogResult.OK };
        pnlButtons.Controls.AddRange(new Control[] { btnCancel, btnOk });

        layout.Controls.Add(lblInstructions, 0, 0);
        layout.Controls.Add(lblState,        0, 1);
        layout.Controls.Add(pnlButtons,      0, 2);
        Controls.Add(layout);

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        UpdateState();
        _pollTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _pollTimer.Tick += (_, _) => UpdateState();
        _pollTimer.Start();
        FormClosed += (_, _) => _pollTimer.Dispose();
    }

    private void UpdateState()
    {
        NeoDriverKind kind = NeoDriverState.Detect().Hid;
        bool ready = kind is NeoDriverKind.WinUsb or NeoDriverKind.WindowsHid;
        string desc = kind switch
        {
            NeoDriverKind.NotSeen             => "not seen yet -- plug in the Neo",
            NeoDriverKind.WindowsHid          => "recognised as a keyboard -- ready!",
            NeoDriverKind.WinUsb              => "recognised (WinUSB) -- ready!",
            NeoDriverKind.RenaissanceLearning => "using the old AlphaSmart Manager driver",
            NeoDriverKind.DriverError         => "driver failed to start",
            NeoDriverKind.Other               => "driver not yet applied",
            _                                  => kind.ToString()
        };
        lblState.Text      = $"Current state: {desc}";
        lblState.ForeColor = ready ? Color.DarkGreen : Color.Firebrick;
    }
}
