namespace InventoryManager;

partial class AddItemDialog
{
    private System.ComponentModel.IContainer components = null!;

    private Label lblName = null!;
    private TextBox txtName = null!;
    private Label lblCategory = null!;
    private TextBox txtCategory = null!;
    private Label lblQuantity = null!;
    private TextBox txtQuantity = null!;
    private Label lblPrice = null!;
    private TextBox txtPrice = null!;
    private Button btnOk = null!;
    private Button btnCancel = null!;

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

        var yPos = 20;
        const int labelX = 20;
        const int inputX = 110;
        const int inputWidth = 220;
        const int rowHeight = 35;

        // ── Name ───────────────────────────────────────────────────────
        lblName = new Label
        {
            Text = "Name:",
            Location = new Point(labelX, yPos + 3),
            AutoSize = true
        };
        txtName = new TextBox
        {
            Location = new Point(inputX, yPos),
            Width = inputWidth
        };
        yPos += rowHeight;

        // ── Category ───────────────────────────────────────────────────
        lblCategory = new Label
        {
            Text = "Category:",
            Location = new Point(labelX, yPos + 3),
            AutoSize = true
        };
        txtCategory = new TextBox
        {
            Location = new Point(inputX, yPos),
            Width = inputWidth
        };
        yPos += rowHeight;

        // ── Quantity ───────────────────────────────────────────────────
        lblQuantity = new Label
        {
            Text = "Quantity:",
            Location = new Point(labelX, yPos + 3),
            AutoSize = true
        };
        txtQuantity = new TextBox
        {
            Location = new Point(inputX, yPos),
            Width = inputWidth
        };
        yPos += rowHeight;

        // ── Price ──────────────────────────────────────────────────────
        lblPrice = new Label
        {
            Text = "Price:",
            Location = new Point(labelX, yPos + 3),
            AutoSize = true
        };
        txtPrice = new TextBox
        {
            Location = new Point(inputX, yPos),
            Width = inputWidth
        };
        yPos += rowHeight + 10;

        // ── Buttons ────────────────────────────────────────────────────
        btnOk = new Button
        {
            Text = "OK",
            Location = new Point(150, yPos),
            Width = 80,
            DialogResult = DialogResult.None // We handle the click manually
        };
        btnOk.Click += BtnOk_Click;

        btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(240, yPos),
            Width = 80,
            DialogResult = DialogResult.Cancel
        };
        btnCancel.Click += BtnCancel_Click;

        // ── Form ───────────────────────────────────────────────────────
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(370, yPos + 50);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Controls.AddRange(new Control[]
        {
            lblName, txtName,
            lblCategory, txtCategory,
            lblQuantity, txtQuantity,
            lblPrice, txtPrice,
            btnOk, btnCancel
        });
    }
}
