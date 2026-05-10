namespace BeaconExample.InventoryManager;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;

    // ── Controls ────────────────────────────────────────────────────────
    private ToolStrip toolStrip = null!;
    private ToolStripButton btnAdd = null!;
    private ToolStripButton btnEdit = null!;
    private ToolStripButton btnDelete = null!;
    private ToolStripSeparator toolStripSeparator1 = null!;
    private ToolStripButton btnExport = null!;
    private ToolStripSeparator toolStripSeparator2 = null!;
    private ToolStripButton btnAbout = null!;
    private ToolStripSeparator toolStripSeparator3 = null!;
    private ToolStripButton btnTestException = null!;
    private ToolStripSeparator toolStripSeparator4 = null!;
    private ToolStripButton btnExportManifest = null!;
    private ToolStripButton btnTestDeadLetter = null!;
    private ToolStripSeparator toolStripSeparator5 = null!;
    private ToolStripButton btnOptToggle = null!;
    private ToolStripButton btnReset = null!;
    private Panel searchPanel = null!;
    private Label lblSearch = null!;
    private TextBox txtSearch = null!;
    private DataGridView dgvItems = null!;
    private StatusStrip statusStrip = null!;
    private ToolStripStatusLabel tsslBeaconStatus = null!;
    private ToolStripStatusLabel tsslItemCount = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        // ── ToolStrip ──────────────────────────────────────────────────
        toolStrip = new ToolStrip();

        btnAdd = new ToolStripButton
        {
            Text = "Add Item",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Add a new inventory item"
        };
        btnAdd.Click += BtnAdd_Click;

        btnEdit = new ToolStripButton
        {
            Text = "Edit Item",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Edit the selected inventory item"
        };
        btnEdit.Click += BtnEdit_Click;

        btnDelete = new ToolStripButton
        {
            Text = "Delete Item",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Delete the selected inventory item"
        };
        btnDelete.Click += BtnDelete_Click;

        toolStripSeparator1 = new ToolStripSeparator();

        btnExport = new ToolStripButton
        {
            Text = "Export to CSV",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Export inventory to a CSV file"
        };
        btnExport.Click += BtnExport_Click;

        toolStripSeparator2 = new ToolStripSeparator();

        btnAbout = new ToolStripButton
        {
            Text = "About",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "About this application"
        };
        btnAbout.Click += BtnAbout_Click;

        toolStripSeparator3 = new ToolStripSeparator();

        btnTestException = new ToolStripButton
        {
            Text = "Test Exception",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Simulate an exception to test Beacon error reporting"
        };
        btnTestException.Click += BtnTestException_Click;

        toolStripSeparator4 = new ToolStripSeparator();

        btnExportManifest = new ToolStripButton
        {
            Text = "Export Manifest",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Export event manifest JSON for the portal's allowlist import"
        };
        btnExportManifest.Click += BtnExportManifest_Click;

        btnTestDeadLetter = new ToolStripButton
        {
            Text = "Send Invalid Event",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Send an invalid event not in the allowlist to trigger a dead letter"
        };
        btnTestDeadLetter.Click += BtnTestDeadLetter_Click;

        toolStripSeparator5 = new ToolStripSeparator();

        btnOptToggle = new ToolStripButton
        {
            Text = "Opt Out/In",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Toggle tracking consent (OptOut/OptIn)"
        };
        btnOptToggle.Click += BtnOptToggle_Click;

        btnReset = new ToolStripButton
        {
            Text = "Reset",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Reset all SDK state (identity, session, queue, device ID)"
        };
        btnReset.Click += BtnReset_Click;

        toolStrip.Items.AddRange(new ToolStripItem[]
        {
            btnAdd, btnEdit, btnDelete,
            toolStripSeparator1,
            btnExport,
            toolStripSeparator2,
            btnAbout,
            toolStripSeparator3,
            btnTestException,
            toolStripSeparator4,
            btnExportManifest,
            btnTestDeadLetter,
            toolStripSeparator5,
            btnOptToggle,
            btnReset
        });

        // ── Search Panel ───────────────────────────────────────────────
        searchPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 8, 8, 4)
        };

        lblSearch = new Label
        {
            Text = "Search:",
            AutoSize = true,
            Location = new Point(10, 12)
        };

        txtSearch = new TextBox
        {
            Location = new Point(65, 9),
            Width = 300,
            PlaceholderText = "Filter by name or category..."
        };
        txtSearch.TextChanged += TxtSearch_TextChanged;

        searchPanel.Controls.Add(lblSearch);
        searchPanel.Controls.Add(txtSearch);

        // ── DataGridView ───────────────────────────────────────────────
        dgvItems = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None
        };

        // ── StatusStrip ────────────────────────────────────────────────
        statusStrip = new StatusStrip();

        tsslBeaconStatus = new ToolStripStatusLabel
        {
            Text = "Beacon: ...",
            Spring = false,
            BorderSides = ToolStripStatusLabelBorderSides.Right
        };

        tsslItemCount = new ToolStripStatusLabel
        {
            Text = "Items: 0",
            Spring = true,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };

        statusStrip.Items.AddRange(new ToolStripItem[]
        {
            tsslBeaconStatus,
            tsslItemCount
        });

        // ── Form ───────────────────────────────────────────────────────
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(900, 550);
        Text = "Beacon Example - Inventory Manager";
        StartPosition = FormStartPosition.CenterScreen;

        // Add controls in correct order (bottom to top for docking)
        Controls.Add(dgvItems);
        Controls.Add(searchPanel);
        Controls.Add(toolStrip);
        Controls.Add(statusStrip);

        // ── Events ─────────────────────────────────────────────────────
        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;
    }
}
