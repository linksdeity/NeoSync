namespace NeoSync.App;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;

    private Button               btnAdvanced   = null!;
    private ToolStripMenuItem     mnuShowSystem = null!;
    private ToolStripMenuItem     mnuDumpDiag   = null!;
    private ContextMenuStrip     menuAdvanced  = null!;
    private TreeView             treeFiles     = null!;
    private Button               btnConnect    = null!;
    private Button               btnDownload   = null!;
    private Button               btnUpload     = null!;
    private StatusStrip          statusStrip   = null!;
    private ToolStripStatusLabel lblStatus     = null!;
    private FlowLayoutPanel      pnlButtons    = null!;
    private TableLayoutPanel     tableLayout   = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            _device?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        SuspendLayout();

        Text          = "NeoSync";
        MinimumSize   = new Size(460, 520);
        Size          = new Size(520, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font          = new Font("Segoe UI", 9f);

        statusStrip = new StatusStrip { Dock = DockStyle.Bottom };
        lblStatus   = new ToolStripStatusLabel("Not connected.");
        statusStrip.Items.Add(lblStatus);

        var btnPad = new Padding(0, 0, 6, 0);
        btnConnect  = new Button { Text = "Connect",  AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        btnDownload = new Button { Text = "Download", AutoSize = true, Margin = btnPad, Enabled = false };
        btnUpload   = new Button { Text = "Upload",   AutoSize = true, Margin = btnPad, Enabled = false };

        btnAdvanced = new Button
        {
            Text     = "Advanced ▼",
            AutoSize = true,
            Margin   = new Padding(0, 0, 6, 0)
        };
        menuAdvanced = new ContextMenuStrip(components);
        mnuShowSystem = new ToolStripMenuItem("Show all applets") { CheckOnClick = true };
        mnuShowSystem.CheckedChanged += (_, _) => { if (_applets.Count > 0) PopulateTree(_applets); };
        menuAdvanced.Items.Add(mnuShowSystem);
        menuAdvanced.Items.Add(new ToolStripSeparator());
        menuAdvanced.Items.Add("Reinstall WinUSB drivers…", null, (_, _) => ShowDriverSetup());        menuAdvanced.Items.Add("Remove WinUSB drivers\u2026",    null, (_, _) => RemoveDrivers());
        menuAdvanced.Items.Add(new ToolStripSeparator());
        mnuDumpDiag = new ToolStripMenuItem("Dump USB diagnostics\u2026");
        mnuDumpDiag.Click += (_, _) => DumpUsbDiagnostics();
        menuAdvanced.Items.Add(mnuDumpDiag);
        pnlButtons = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            Padding       = new Padding(6, 6, 6, 4),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoSize      = false
        };
        pnlButtons.Controls.AddRange(
            new Control[] { btnConnect, btnDownload, btnUpload, btnAdvanced });

        treeFiles = new TreeView
        {
            Dock          = DockStyle.Fill,
            ShowLines     = true,
            ShowRootLines = true,
            ShowPlusMinus = true,
            Font          = new Font("Segoe UI", 9f)
        };

        tableLayout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 2,
            Padding     = new Padding(0)
        };
        tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tableLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tableLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tableLayout.Controls.Add(pnlButtons, 0, 0);
        tableLayout.Controls.Add(treeFiles,  0, 1);

        btnConnect.Click    += btnConnect_Click;
        btnDownload.Click   += btnDownload_Click;
        btnUpload.Click     += btnUpload_Click;
        btnAdvanced.Click   += (_, _) => menuAdvanced.Show(btnAdvanced, 0, btnAdvanced.Height);
        treeFiles.AfterSelect += treeFiles_AfterSelect;

        Controls.Add(statusStrip);
        Controls.Add(tableLayout);

        ResumeLayout(false);
        PerformLayout();
    }
}
