namespace InventoryManager;

partial class AboutDialog
{
    private System.ComponentModel.IContainer components = null!;

    private Label lblTitle = null!;
    private Label lblVersion = null!;
    private Label lblDescription = null!;
    private Button btnClose = null!;

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

        // ── Title ──────────────────────────────────────────────────────
        lblTitle = new Label
        {
            Text = "Inventory Manager",
            Font = new Font(Font.FontFamily, 14F, FontStyle.Bold),
            Location = new Point(20, 20),
            AutoSize = true
        };

        // ── Version ───────────────────────────────────────────────────
        lblVersion = new Label
        {
            Text = "Version 1.0.0",
            Location = new Point(20, 55),
            AutoSize = true
        };

        // ── Description ───────────────────────────────────────────────
        lblDescription = new Label
        {
            Text = "A sample WinForms inventory app.\n\n" +
                   "This is the un-instrumented starting point for the\n" +
                   "SoftAgility Beacon \"add usage tracking in 30 minutes\"\n" +
                   "tutorial. It has no analytics yet. Follow the tutorial\n" +
                   "to instrument it with the Beacon SDK:\n\n" +
                   "  - Configure the SDK on startup\n" +
                   "  - Identify a user and start a session\n" +
                   "  - Track events when items change and exports run\n" +
                   "  - Report handled exceptions\n" +
                   "  - Dispose cleanly on shutdown",
            Location = new Point(20, 80),
            AutoSize = true
        };

        // Measure the auto-sized description so the button and the form size to
        // fit it, regardless of line count, font, or DPI. (The previous fixed
        // ClientSize / button position let the longer text overlap the button.)
        var descSize = lblDescription.PreferredSize;

        // ── Close Button ──────────────────────────────────────────────
        btnClose = new Button
        {
            Text = "Close",
            Width = 80,
            Location = new Point(20 + (descSize.Width - 80) / 2, 80 + descSize.Height + 16),
            DialogResult = DialogResult.OK
        };
        btnClose.Click += BtnClose_Click;

        // ── Form ───────────────────────────────────────────────────────
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(descSize.Width + 40, btnClose.Bottom + 16);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "About";
        AcceptButton = btnClose;
        CancelButton = btnClose;

        Controls.AddRange(new Control[]
        {
            lblTitle, lblVersion, lblDescription, btnClose
        });
    }
}
