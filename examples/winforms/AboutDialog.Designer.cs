namespace BeaconExample.InventoryManager;

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
            Text = "Beacon Example - Inventory Manager",
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
            Text = "A sample WinForms application demonstrating\n" +
                   "integration with the SoftAgility Beacon SDK.\n\n" +
                   "This app shows how to:\n" +
                   "  - Initialize the SDK (Configure / AddBeacon)\n" +
                   "  - Start and end sessions\n" +
                   "  - Track user events with properties\n" +
                   "  - Display flush status in the UI\n" +
                   "  - Handle graceful shutdown",
            Location = new Point(20, 80),
            AutoSize = true
        };

        // ── Close Button ──────────────────────────────────────────────
        btnClose = new Button
        {
            Text = "Close",
            Width = 80,
            Location = new Point(140, 230),
            DialogResult = DialogResult.OK
        };
        btnClose.Click += BtnClose_Click;

        // ── Form ───────────────────────────────────────────────────────
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(360, 280);
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
