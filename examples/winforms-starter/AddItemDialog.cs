using InventoryManager.Models;

namespace InventoryManager;

/// <summary>
/// Modal dialog for adding or editing an inventory item. Plain WinForms — no
/// analytics. (In the tutorial, the Track() call happens back in MainForm after
/// this dialog returns DialogResult.OK, not in the dialog itself.)
/// </summary>
public partial class AddItemDialog : Form
{
    public string ItemName => txtName.Text.Trim();
    public string ItemCategory => txtCategory.Text.Trim();
    public int ItemQuantity => int.TryParse(txtQuantity.Text, out var q) ? q : 0;
    public decimal ItemPrice => decimal.TryParse(txtPrice.Text, out var p) ? p : 0m;

    /// <summary>
    /// Creates a new Add Item dialog (blank fields).
    /// </summary>
    public AddItemDialog()
    {
        InitializeComponent();
        Text = "Add Item";
    }

    /// <summary>
    /// Creates an Edit Item dialog (pre-populated with existing item values).
    /// </summary>
    public AddItemDialog(InventoryItem item) : this()
    {
        Text = "Edit Item";
        txtName.Text = item.Name;
        txtCategory.Text = item.Category;
        txtQuantity.Text = item.Quantity.ToString();
        txtPrice.Text = item.Price.ToString("F2");
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtName.Text))
        {
            MessageBox.Show("Name is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void BtnCancel_Click(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }
}
