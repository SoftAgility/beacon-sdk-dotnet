namespace InventoryManager;

/// <summary>
/// Simple About dialog showing the application name and version. Plain WinForms,
/// no analytics. (In the tutorial, the navigation Track() call happens in
/// MainForm when the About button is clicked, before this dialog is shown.)
/// </summary>
public partial class AboutDialog : Form
{
    public AboutDialog()
    {
        InitializeComponent();
    }

    private void BtnClose_Click(object? sender, EventArgs e)
    {
        Close();
    }
}
