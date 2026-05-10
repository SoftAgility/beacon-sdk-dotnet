namespace BeaconExample.InventoryManager;

/// <summary>
/// Simple About dialog showing the application name and version.
/// This dialog has no Beacon integration — the Track() call for
/// "dialog_opened" happens in MainForm when the user clicks the
/// About button, before this dialog is shown.
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
