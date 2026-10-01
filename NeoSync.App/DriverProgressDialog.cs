namespace NeoSync.App;

/// <summary>
/// Single progress dialog for the whole install/reinstall sequence -- staging, waiting for
/// the Neo to be plugged in (only if it isn't already), and the mode switch -- shown as one
/// continuously-updated status line instead of a string of separate popups.
/// </summary>
internal sealed class DriverProgressDialog : Form
{
    private Label lblStatus = null!;
    private Button btnAction = null!;

    /// <summary>Set once the user clicks Cancel while an operation is still in progress.
    /// The background worker driving this dialog is expected to poll this and stop.</summary>
    public bool Cancelled { get; private set; }

    public DriverProgressDialog()
    {
        Text            = "NeoSync - Driver Setup";
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;
        MinimumSize     = new Size(420, 0);
        StartPosition   = FormStartPosition.CenterParent;
        Font            = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ControlBox      = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        lblStatus = new Label
        {
            AutoSize    = true,
            MaximumSize = new Size(420, 0),
            Font        = new Font("Segoe UI", 9f, FontStyle.Bold),
            Margin      = new Padding(0, 0, 0, 12)
        };

        var pnlButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            Anchor        = AnchorStyles.Right
        };
        btnAction = new Button { Text = "Cancel", AutoSize = true };
        btnAction.Click += (_, _) =>
        {
            if (btnAction.Text == "Cancel") Cancelled = true;
            else base.Close();
        };
        pnlButtons.Controls.Add(btnAction);

        layout.Controls.Add(lblStatus,  0, 0);
        layout.Controls.Add(pnlButtons, 0, 1);
        Controls.Add(layout);

        CancelButton = btnAction;
        // Alt+F4 (or any other close route) should behave like Cancel, not leave the
        // background worker driving this dialog orphaned mid-operation.
        FormClosing += (_, _) => Cancelled = true;
    }

    /// <summary>Updates the status line. Safe to call from a background thread.</summary>
    public void SetStatus(string text, Color color)
    {
        if (InvokeRequired) { Invoke(() => SetStatus(text, color)); return; }
        lblStatus.Text      = text;
        lblStatus.ForeColor = color;
    }

    /// <summary>Switches the button from Cancel to Close once the operation has a final
    /// result (success or failure), so it no longer reads as still cancellable.</summary>
    public void SetFinished()
    {
        if (InvokeRequired) { Invoke(SetFinished); return; }
        btnAction.Text = "Close";
    }

    /// <summary>Closes the dialog. Safe to call from a background thread.</summary>
    public void CloseSafe()
    {
        if (InvokeRequired) { Invoke(CloseSafe); return; }
        base.Close();
    }
}
