using System.Text;
using InventoryManager.Models;

namespace InventoryManager;

/// <summary>
/// Main application form. A plain, working inventory manager with no analytics.
/// This is the starting point for the Beacon "add usage tracking in 30 minutes"
/// tutorial — each handler below is a natural place to add a Track() call. The
/// inline TUTORIAL comments mark exactly where each step of the tutorial slots in.
/// </summary>
public partial class MainForm : Form
{
    // ── Application State ──────────────────────────────────────────────
    private readonly List<InventoryItem> _items = [];
    private int _nextId = 1;

    public MainForm()
    {
        // TUTORIAL — Step 3: take an IBeaconTracker (constructor arg or
        //                    BeaconTracker.Instance) and call Identify(...) here.
        InitializeComponent();

        // Seed some demo data so the grid isn't empty on first run.
        SeedDemoData();
    }

    // ── Form Lifecycle ─────────────────────────────────────────────────

    private void MainForm_Load(object? sender, EventArgs e)
    {
        // TUTORIAL — Step 3: call StartSession() here, once the form is up.
        RefreshGrid();
    }

    // TUTORIAL — Step 6: add a MainForm_FormClosing handler (wired in the
    //            designer) that disposes the tracker so queued events are
    //            flushed to disk on shutdown.

    // ── Toolbar Button Handlers ────────────────────────────────────────

    private void BtnAdd_Click(object? sender, EventArgs e)
    {
        using var dialog = new AddItemDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var item = new InventoryItem
            {
                Id = _nextId++,
                Name = dialog.ItemName,
                Category = dialog.ItemCategory,
                Quantity = dialog.ItemQuantity,
                Price = dialog.ItemPrice
            };

            _items.Add(item);
            RefreshGrid();

            // TUTORIAL — Step 4: Track("inventory", "item_added", new { ... }) here.
            // Send the category and quantity — not the item name (it may be PII).
        }
    }

    private void BtnEdit_Click(object? sender, EventArgs e)
    {
        if (dgvItems.CurrentRow?.DataBoundItem is not InventoryItem item)
            return;

        using var dialog = new AddItemDialog(item);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            item.Name = dialog.ItemName;
            item.Category = dialog.ItemCategory;
            item.Quantity = dialog.ItemQuantity;
            item.Price = dialog.ItemPrice;
            RefreshGrid();

            // TUTORIAL — Step 4 (optional): Track("inventory", "item_edited") here.
        }
    }

    private void BtnDelete_Click(object? sender, EventArgs e)
    {
        if (dgvItems.CurrentRow?.DataBoundItem is not InventoryItem item)
            return;

        var result = MessageBox.Show(
            $"Delete '{item.Name}'?",
            "Confirm Delete",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
        {
            _items.Remove(item);
            RefreshGrid();

            // TUTORIAL — Step 4 (optional): Track("inventory", "item_deleted") here.
        }
    }

    private void BtnExport_Click(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            DefaultExt = "csv",
            FileName = "inventory_export.csv"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Id,Name,Category,Quantity,Price");
                foreach (var item in _items)
                {
                    sb.AppendLine($"{item.Id},\"{item.Name}\",\"{item.Category}\",{item.Quantity},{item.Price}");
                }
                File.WriteAllText(dialog.FileName, sb.ToString());

                // TUTORIAL — Step 4: Track("reporting", "csv_exported", new { row_count = _items.Count }) here.

                MessageBox.Show($"Exported {_items.Count} items.", "Export Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                // TUTORIAL — Step 5: TrackException(ex, ExceptionSeverity.NonFatal) here —
                // a handled, noteworthy failure worth reporting.
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void BtnAbout_Click(object? sender, EventArgs e)
    {
        // TUTORIAL — Step 4: Track a navigation event (e.g. "navigation", "dialog_opened") here.
        using var dialog = new AboutDialog();
        dialog.ShowDialog(this);
    }

    // ── Search ─────────────────────────────────────────────────────────

    private void TxtSearch_TextChanged(object? sender, EventArgs e)
    {
        RefreshGrid();
    }

    // ── Grid Helpers ───────────────────────────────────────────────────

    private void RefreshGrid()
    {
        var filtered = GetFilteredItems();
        dgvItems.DataSource = null;
        dgvItems.DataSource = filtered;
        tsslItemCount.Text = $"Items: {_items.Count}";
    }

    private List<InventoryItem> GetFilteredItems()
    {
        var query = txtSearch.Text.Trim();
        if (string.IsNullOrEmpty(query))
            return [.. _items];

        return _items
            .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        i.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ── Demo Data ──────────────────────────────────────────────────────

    private void SeedDemoData()
    {
        _items.AddRange([
            new() { Id = _nextId++, Name = "Widget A", Category = "Widgets", Quantity = 150, Price = 9.99m },
            new() { Id = _nextId++, Name = "Widget B", Category = "Widgets", Quantity = 75, Price = 14.50m },
            new() { Id = _nextId++, Name = "Gadget X", Category = "Gadgets", Quantity = 30, Price = 29.99m },
            new() { Id = _nextId++, Name = "Gadget Y", Category = "Gadgets", Quantity = 12, Price = 49.99m },
            new() { Id = _nextId++, Name = "Supply Pack", Category = "Supplies", Quantity = 200, Price = 4.99m },
        ]);
    }
}
