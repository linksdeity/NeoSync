using NeoSync.Core.Usb;

namespace NeoSync.App;

/// <summary>
/// Shown when one or both Neo USB drivers are not ready.
/// Explains what's needed and offers guided setup or manual instructions.
/// </summary>
internal sealed class DriverSetupForm : Form
{
    private NeoDriverState _state;
    private Label lblExplanation = null!;
    private FlowLayoutPanel pnlStatus = null!;

    public DriverSetupForm(NeoDriverState state)
    {
        _state = state;
        InitializeUi();
    }

    private void InitializeUi()
    {
        Text            = "NeoSync - Driver Setup";
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;
        MinimumSize     = new Size(560, 0);
        StartPosition   = FormStartPosition.CenterParent;
        Font            = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ── Heading ───────────────────────────────────────────────────────
        var lblHeading = new Label
        {
            Text      = "WinUSB Driver Setup",
            Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize  = true,
            Margin    = new Padding(0, 0, 0, 8)
        };

        // ── Explanation ───────────────────────────────────────────────────
        // AutoSize Label with a bounded width (not a Dock=Fill RichTextBox in a
        // Percent row) so the AutoSize form can compute a real preferred size -
        // this is what was causing text/buttons to render clipped previously.
        lblExplanation = new Label
        {
            AutoSize      = true,
            MaximumSize   = new Size(560, 0),
            Font          = new Font("Segoe UI", 9f),
            Text          = BuildExplanationText(),
            Margin        = new Padding(0, 0, 0, 8)
        };

        // ── Status panel ─────────────────────────────────────────────────
        pnlStatus = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoSize      = true,
            Margin        = new Padding(0, 8, 0, 8)
        };
        RefreshStatusLabels();

        // ── Buttons ───────────────────────────────────────────────────────
        var pnlButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            Anchor        = AnchorStyles.Right
        };

        var btnClose = new Button
        {
            Text         = "Close",
            AutoSize     = true,
            DialogResult = DialogResult.Cancel,
            Margin       = new Padding(0, 0, 6, 0)
        };

        var btnAutoInstall = new Button
        {
            Text     = "Install drivers automatically",
            AutoSize = true,
            Margin   = new Padding(0, 0, 6, 0)
        };
        btnAutoInstall.Click += (_, _) => DriverInstallFlow.Run(this, RefreshStatusLabels);

        pnlButtons.Controls.AddRange(new Control[] { btnClose, btnAutoInstall });

        layout.Controls.Add(lblHeading,      0, 0);
        layout.Controls.Add(lblExplanation,  0, 1);
        layout.Controls.Add(pnlStatus,       0, 2);
        layout.Controls.Add(pnlButtons,      0, 3);

        Controls.Add(layout);
        AcceptButton = btnClose;
        CancelButton = btnClose;
    }

    /// <summary>Re-detects driver state and refreshes the heading text and status labels
    /// in place -- called after Install so the green/red shown actually reflects what
    /// just happened, instead of only ever showing state from when the dialog opened.</summary>
    private void RefreshStatusLabels()
    {
        _state = NeoDriverState.Detect();
        lblExplanation.Text = BuildExplanationText();

        pnlStatus.SuspendLayout();
        pnlStatus.Controls.Clear();
        pnlStatus.Controls.Add(MakeStatusLabel("BD04 (mode-switch driver)", _state.Hid));
        pnlStatus.Controls.Add(MakeStatusLabel("BD01 (transfer mode driver)", _state.Comms));
        pnlStatus.ResumeLayout();
    }

    private string BuildExplanationText()
    {
        var lines = new List<string>
        {
            "Use this to reinstall broken or missing WinUSB drivers without a full NeoSync reinstall.\r\n"
        };

        if (_state.HidReady && _state.CommsReady)
        {
            lines.Add("Your drivers currently look fine -- you only need this if you're having connection problems.\r\n");
        }

        if (!_state.HidReady)
        {
            string current = _state.Hid switch
            {
                NeoDriverKind.NotSeen            => "not yet seen on this PC",
                NeoDriverKind.WindowsHid         => "using the Windows keyboard driver",
                NeoDriverKind.RenaissanceLearning => "using the old AlphaSmart Manager driver",
                NeoDriverKind.DriverError        => "WinUSB failed to start - often clears itself, try Connect again first",
                NeoDriverKind.Other              => "driver was removed or replaced - reinstall needed",
                _                                => $"driver state: {_state.Hid}"
            };
            lines.Add($"\u2022 Keyboard mode (BD04): {current}.\r\n");
        }

        if (!_state.CommsReady)
        {
            string current = _state.Comms switch
            {
                NeoDriverKind.NotSeen            => "not yet seen on this PC",
                NeoDriverKind.RenaissanceLearning => "using the old AlphaSmart Manager driver",
                NeoDriverKind.DriverError        => "WinUSB failed to start - often clears itself, try Connect again first",
                NeoDriverKind.Other              => "driver was removed or replaced - reinstall needed",
                _                                => $"driver state: {_state.Comms}"
            };
            lines.Add($"\u2022 Transfer mode (BD01): {current}.\r\n");
        }

        lines.Add("\r\nClick \"Install drivers automatically\" below. Administrator rights are required.");

        return string.Join("", lines);
    }

    /// <summary>Builds a status label for a BD04/BD01 driver kind -- shared with
    /// DriverProgressDialog and RepairNeoSyncForm so all three show identical wording.</summary>
    internal static Label MakeStatusLabel(string name, NeoDriverKind kind)
    {
        string icon, desc;
        Color col;
        switch (kind)
        {
            case NeoDriverKind.WinUsb:
                icon = "\u2713"; col = Color.DarkGreen; desc = "WinUSB - ready"; break;
            case NeoDriverKind.WindowsHid:
                // Fine as a keyboard right now, but Connect still has to force-install WinUSB
                // the first time -- not broken, just not as ready as WinUsb is.
                icon = "\u26A0"; col = Color.DarkOrange;
                desc = "Windows HID keyboard - WinUSB install needed on first Connect"; break;
            case NeoDriverKind.DriverError:
                // CM_PROB_FAILED_START right after a mode switch or Neo reboot is usually
                // transient and already self-heals via a soft device restart elsewhere in
                // the app (Connect's own retry, or the post-Disconnect cleanup) - confirmed
                // by repro where this showed red here but Connect worked fine moments later.
                // Reinstalling is only the right call if it's still stuck after retrying.
                icon = "\u26A0"; col = Color.DarkOrange;
                desc = "WinUSB failed to start - often clears itself; try Connect again first"; break;
            case NeoDriverKind.NotSeen:
                icon = "\u2717"; col = Color.Firebrick; desc = "not seen - needs setup"; break;
            case NeoDriverKind.RenaissanceLearning:
                icon = "\u2717"; col = Color.Firebrick; desc = "AlphaSmart Manager driver - needs replacing"; break;
            case NeoDriverKind.Other:
                icon = "\u2717"; col = Color.Firebrick; desc = "driver removed or unsupported - needs WinUSB"; break;
            default:
                icon = "\u2717"; col = Color.Firebrick; desc = $"{kind} - needs WinUSB"; break;
        }
        return new Label
        {
            Text      = $"  {icon}  {name}: {desc}",
            AutoSize  = true,
            ForeColor = col,
            Margin    = new Padding(0, 2, 0, 2)
        };
    }
}