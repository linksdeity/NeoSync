using NeoSync.Core.Ipc;

namespace NeoSync.App;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;

    private Button               btnAdvanced   = null!;
    private ToolStripMenuItem     mnuShowSystem = null!;
    private ToolStripMenuItem     mnuCopyDiagnostics = null!;
    private ToolStripMenuItem     mnuDiagLogging = null!;
    private ContextMenuStrip     menuAdvanced  = null!;
    private TreeView             treeFiles     = null!;
    private Button               btnConnect    = null!;
    private Button               btnDownload   = null!;
    private Button               btnUpload     = null!;
    private Button               btnDelete     = null!;
    private SlideToggleButton    btnQuickSync  = null!;
    private ToolStripMenuItem     mnuQuickSyncFolder = null!;
    private StatusStrip          statusStrip   = null!;
    private ToolStripStatusLabel lblStatus     = null!;
    private Panel                pnlUsageBar   = null!;
    private FlowLayoutPanel      pnlButtons    = null!;
    private TableLayoutPanel     tableLayout   = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            _device?.Dispose();
            _deviceWatcher.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        SuspendLayout();

        Text          = "NeoSync";
        MinimumSize   = new Size(700, 620);
        Size          = new Size(760, 860);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Font          = new Font("Segoe UI", 9f);

        statusStrip = new StatusStrip { Dock = DockStyle.Bottom };
        lblStatus   = new ToolStripStatusLabel("Not connected.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(lblStatus);

        pnlUsageBar = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Visible = false };
        pnlUsageBar.Paint += pnlUsageBar_Paint;

        var btnPad = new Padding(0, 0, 6, 0);
        btnConnect  = new Button { Text = "Connect",  AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        btnDownload = new Button { Text = "Download", AutoSize = true, Margin = btnPad, Enabled = false };
        btnUpload   = new Button { Text = "Upload",   AutoSize = true, Margin = btnPad, Enabled = false };
        btnDelete   = new Button { Text = "Delete",   AutoSize = true, Margin = btnPad, Enabled = false };

        btnQuickSync = new SlideToggleButton("Quick Sync", "Open Folder")
        {
            Font           = Font,
            Margin         = new Padding(16, 0, 6, 0),
            PrimaryEnabled = false
        };
        btnQuickSync.SizeToFit();

        btnAdvanced = new Button
        {
            Text     = "Advanced ▼",
            AutoSize = true,
            Margin   = new Padding(0, 0, 6, 0)
        };
        menuAdvanced = new ContextMenuStrip(components);
        // Disabled for now: non-AlphaWord applets have no real functionality here yet
        // (no preview, no Quick Sync support) -- re-enable once that's built out.
        mnuShowSystem = new ToolStripMenuItem("Show all applets") { CheckOnClick = true, Enabled = false };
        mnuShowSystem.CheckedChanged += (_, _) => { if (_applets.Count > 0) PopulateTree(_applets); };
        menuAdvanced.Items.Add(mnuShowSystem);
        menuAdvanced.Items.Add(new ToolStripSeparator());
        menuAdvanced.Items.Add("Reinstall WinUSB drivers…", null, (_, _) => DriverInstallFlow.Run(this));
        menuAdvanced.Items.Add("Remove WinUSB drivers\u2026",    null, (_, _) => RemoveDrivers());
        menuAdvanced.Items.Add("Repair NeoSyncHelper service\u2026", null, (_, _) => ServiceRepair.Run(this));
        menuAdvanced.Items.Add(new ToolStripSeparator());
        mnuCopyDiagnostics = new ToolStripMenuItem("Copy Diagnostics…");
        mnuCopyDiagnostics.Click += (_, _) => CopyDiagnostics();
        menuAdvanced.Items.Add(mnuCopyDiagnostics);
        mnuDiagLogging = new ToolStripMenuItem("Enable diagnostic logging") { CheckOnClick = true };
        mnuDiagLogging.Checked = NeoSyncHelperClient.Send("GET_DIAGNOSTICS") != "0";
        mnuDiagLogging.Click += (_, _) => NeoSyncHelperClient.Send($"SET_DIAGNOSTICS {(mnuDiagLogging.Checked ? 1 : 0)}");
        menuAdvanced.Items.Add(mnuDiagLogging);
        menuAdvanced.Items.Add(new ToolStripSeparator());
        mnuQuickSyncFolder = new ToolStripMenuItem("Change Quick Sync folder…");
        mnuQuickSyncFolder.Click += (_, _) => ChangeQuickSyncFolder();
        menuAdvanced.Items.Add(mnuQuickSyncFolder);
        menuAdvanced.Items.Add(new ToolStripSeparator());
        menuAdvanced.Items.Add("Check for Updates…", null, async (_, _) => await UpdateChecker.CheckAsync(this));
        menuAdvanced.Items.Add($"About NeoSync (v{UpdateChecker.CurrentVersion})", null,
            (_, _) => MessageBox.Show(this, $"NeoSync v{UpdateChecker.CurrentVersion}",
                "NeoSync - About", MessageBoxButtons.OK, MessageBoxIcon.Information));
        pnlButtons = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            Padding       = new Padding(6, 6, 6, 4),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoSize      = false
        };
        pnlButtons.Controls.AddRange(
            new Control[] { btnConnect, btnDownload, btnUpload, btnDelete, btnQuickSync, btnAdvanced });
        // btnConnect only reaches its true AutoSize height once parented and its font is
        // inherited from the Form -- copying it before this point captures a stale value.
        btnQuickSync.Height = btnConnect.Height;

        treeFiles = new TreeView
        {
            Dock             = DockStyle.Fill,
            ShowLines        = true,
            ShowRootLines    = true,
            ShowPlusMinus    = true,
            ShowNodeToolTips = true,
            Font             = new Font("Segoe UI", 9f)
        };

        tableLayout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 3,
            Padding     = new Padding(0)
        };
        tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tableLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tableLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        tableLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tableLayout.Controls.Add(pnlButtons,  0, 0);
        tableLayout.Controls.Add(pnlUsageBar, 0, 1);
        tableLayout.Controls.Add(treeFiles,   0, 2);

        btnConnect.Click    += btnConnect_Click;
        btnDownload.Click   += btnDownload_Click;
        btnUpload.Click     += btnUpload_Click;
        btnDelete.Click     += btnDelete_Click;
        btnQuickSync.PrimaryClicked   += btnQuickSync_Click;
        btnQuickSync.SecondaryClicked += (_, _) => OpenQuickSyncFolder();
        btnAdvanced.Click   += (_, _) => menuAdvanced.Show(btnAdvanced, 0, btnAdvanced.Height);
        treeFiles.AfterSelect += treeFiles_AfterSelect;

        Controls.Add(statusStrip);
        Controls.Add(tableLayout);

        ResumeLayout(false);
        PerformLayout();
    }
}
