using NeoSync.Core.Ipc;
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
            Text     = "Install drivers automatically (~20s)",
            AutoSize = true,
            Margin   = new Padding(0, 0, 6, 0)
        };
        btnAutoInstall.Click += OnAutoInstall;

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
                NeoDriverKind.DriverError        => "driver failed to start - reinstall needed",
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
                NeoDriverKind.DriverError        => "driver failed to start - reinstall needed",
                NeoDriverKind.Other              => "driver was removed or replaced - reinstall needed",
                _                                => $"driver state: {_state.Comms}"
            };
            lines.Add($"\u2022 Transfer mode (BD01): {current}.\r\n");
        }

        lines.Add("\r\nClick \"Install drivers automatically\" below. Administrator rights are required.");

        return string.Join("", lines);
    }

    private static Label MakeStatusLabel(string name, NeoDriverKind kind)
    {
        bool ok   = kind == NeoDriverKind.WinUsb || kind == NeoDriverKind.WindowsHid;
        string icon = ok ? "✓" : "✗";
        Color  col  = ok ? Color.DarkGreen : Color.Firebrick;
        string desc = kind switch
        {
            NeoDriverKind.WinUsb             => "WinUSB - ready",
            NeoDriverKind.DriverError        => "WinUSB failed to start - reinstall needed",
            NeoDriverKind.NotSeen            => "not seen - needs setup",
            NeoDriverKind.WindowsHid         => "Windows HID keyboard - ready",
            NeoDriverKind.RenaissanceLearning => "AlphaSmart Manager driver - needs replacing",
            NeoDriverKind.Other              => "driver removed or unsupported - needs WinUSB",
            _                                => $"{kind} - needs WinUSB"
        };
        return new Label
        {
            Text      = $"  {icon}  {name}: {desc}",
            AutoSize  = true,
            ForeColor = col,
            Margin    = new Padding(0, 2, 0, 2)
        };
    }

    private void InstallViaPnp(string driverDir)
    {
        try
        {
            Cursor  = Cursors.WaitCursor;
            Enabled = false;

            // Clear any existing staged package first, same as a machine that's never had
            // NeoSync installed -- this is the exact same clean-slate state a fresh install
            // starts from, so "Reinstall" gets the same reliability rather than potentially
            // layering a fresh install on top of a stale/partial one.
            NeoSyncHelperClient.Send("REMOVE_DRIVERS");

            // Trust the cert and stage the BD01 INF via the NeoSyncHelper service (LocalSystem).
            string setupResult = NeoSyncHelperClient.Send($"SETUP_DRIVERS {driverDir}");
            RefreshStatusLabels();

            if (!setupResult.StartsWith("OK"))
            {
                MessageBox.Show(this, $"Driver staging failed: {setupResult}",
                    "NeoSync - Driver Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        finally
        {
            Enabled = true;
            Cursor  = Cursors.Default;
        }

        if (new ReplugStatusDialog().ShowDialog(this) == DialogResult.Cancel) return;

        Cursor  = Cursors.WaitCursor;
        Enabled = false;

        // The mode switch and BD01 wait can take up to 30s -- run off the UI thread so a
        // slow or not-quite-ready device can't freeze the dialog (previously blocked here).
        Task.Run(() =>
        {
            string msg;
            try
            {
                // Mode switch via service (handles BD04 driver install, control transfers, cleanup).
                string switchResult = NeoSyncHelperClient.Send("SWITCH");

                if (switchResult != "OK")
                {
                    msg = "The driver has been staged but the mode switch failed (device may not be ready).\n\n" +
                          "Click Connect in the main window - the driver will be applied automatically.";
                }
                else
                {
                    bool bd01Present = false;
                    for (int i = 0; i < 150 && !bd01Present; i++)
                    {
                        bd01Present = NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD01") is not null;
                        if (!bd01Present) Thread.Sleep(200);
                    }
                    msg = bd01Present
                        ? "Driver setup complete. Click Connect in the main window to start."
                        : "The switch succeeded but BD01 took too long to enumerate.\n\nClick Connect in the main window.";
                }
            }
            catch (Exception ex)
            {
                msg = $"Driver setup step failed: {ex.Message}";
            }

            Invoke(() =>
            {
                Enabled = true;
                Cursor  = Cursors.Default;
                // Refresh in place rather than closing -- shows what the driver state actually
                // ended up as, instead of just trusting the completion message.
                RefreshStatusLabels();
                MessageBox.Show(this, msg, "NeoSync - Driver Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        });
    }

    private void OnAutoInstall(object? sender, EventArgs e)
    {
        string driverDir = Path.Combine(AppContext.BaseDirectory, "driver");
        if (!File.Exists(Path.Combine(driverDir, "neosync_bd01.inf")) ||
            !File.Exists(Path.Combine(driverDir, "neosync.cer")))
        {
            MessageBox.Show(this,
                $"Driver files missing from:\r\n{driverDir}\r\n\r\nPlease reinstall NeoSync.",
                "NeoSync - Driver Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        InstallViaPnp(driverDir);
    }
}