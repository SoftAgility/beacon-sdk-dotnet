using System.Text;
using BeaconExample.InventoryManager.Models;
using SoftAgility.Beacon;

namespace BeaconExample.InventoryManager;

/// <summary>
/// Main application form demonstrating comprehensive Beacon SDK integration.
/// Every user interaction that is worth tracking has a corresponding Track() call,
/// showing how to instrument a real desktop application.
/// </summary>
public partial class MainForm : Form
{
    // ── Beacon Integration ─────────────────────────────────────────────
    // This example uses the static singleton pattern (BeaconTracker.Configure +
    // BeaconTracker.Instance). The form owns the tracker's lifetime and disposes
    // it on close. If you use DI (services.AddBeacon()), do NOT dispose from the
    // form — let the DI container manage the tracker's lifetime instead.
    private readonly IBeaconTracker _tracker;

    // ── Application State ──────────────────────────────────────────────
    private readonly List<InventoryItem> _items = [];
    private int _nextId = 1;
    private System.Windows.Forms.Timer? _statusTimer;
    private System.Windows.Forms.Timer? _searchDebounceTimer;

    public MainForm(IBeaconTracker tracker)
    {
        _tracker = tracker;

        // BEACON INTEGRATION: Identify the current user once. All subsequent
        // Track() and StartSession() calls will use this actor ID automatically.
        // In a real app, the user ID would come from authentication.
        _tracker.Identify("demo-user-001");

        InitializeComponent();

        // Seed some demo data
        SeedDemoData();
    }

    // ── Form Lifecycle ─────────────────────────────────────────────────

    /// <summary>
    /// Called when the form loads. This is where we start the Beacon session.
    /// A session groups all events from a single user sitting — from when they
    /// open the app to when they close it.
    /// </summary>
    private void MainForm_Load(object? sender, EventArgs e)
    {
        // BEACON INTEGRATION: Start a session for the identified user.
        // All subsequent Track() calls will automatically include the session_id,
        // so you can analyze user journeys within a single session.
        _tracker.StartSession();

        // Refresh the grid with our demo data
        RefreshGrid();

        // Start a timer that updates the Beacon status indicator in the status bar.
        // The SDK exposes LastFlushStatus which tells you whether the last flush
        // succeeded (Connected), failed (Offline), or is disabled (Disabled).
        _statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _statusTimer.Tick += (_, _) => UpdateBeaconStatus();
        _statusTimer.Start();

        // Set initial status
        UpdateBeaconStatus();
    }

    /// <summary>
    /// Called when the form is closing. Disposes the tracker to persist queued events
    /// to disk and release resources. Only correct for the static singleton pattern —
    /// if using DI, the container owns tracker disposal.
    /// </summary>
    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _statusTimer?.Stop();
        _statusTimer?.Dispose();
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer?.Dispose();

        // BEACON INTEGRATION: Track the app_closed event before disposal.
        _tracker.Track("app", "app_closed");

        // BEACON INTEGRATION: Dispose the tracker (static singleton pattern only).
        // Persists in-memory events to the disk queue for delivery on next launch,
        // clears session state, and closes the offline queue.
        // If using DI, remove this — the host disposes the tracker.
        _tracker.Dispose();
    }

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

            // BEACON INTEGRATION: Track the item_added event with context properties.
            // Properties must be a flat dictionary — no nested objects or arrays.
            // The SDK enforces: max 20 keys, key max 64 chars, value max 256 chars.
            _tracker.Track("inventory", "item_added", new
            {
                item_category = item.Category,
                quantity = item.Quantity
            });
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

            // BEACON INTEGRATION: Track edits with which field changed.
            // In a real app, you would compare old vs new values to determine
            // which fields were actually modified.
            _tracker.Track("inventory", "item_edited", new
            {
                field_changed = "multiple"
            });
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
            var category = item.Category;
            _items.Remove(item);
            RefreshGrid();

            // BEACON INTEGRATION: Track deletions with the item's category.
            // Avoid sending PII (personally identifiable information) in properties.
            // The item name might be PII, so we only send the category.
            _tracker.Track("inventory", "item_deleted", new
            {
                item_category = category
            });
        }
    }

    private void BtnExport_Click(object? sender, EventArgs e)
    {
        // Simple CSV export to demonstrate a reporting event
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

                // BEACON INTEGRATION: Track the CSV export with the number of rows.
                _tracker.Track("reporting", "csv_exported", new
                {
                    row_count = _items.Count
                });

                MessageBox.Show($"Exported {_items.Count} items.", "Export Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                // BEACON INTEGRATION: Report file I/O exceptions as non-fatal.
                // The app continues running — the user can retry with a different path.
                // TrackException automatically includes breadcrumbs (recent Track() calls)
                // and the active session ID, giving you full context for debugging.
                _tracker.TrackException(ex, ExceptionSeverity.NonFatal);

                MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void BtnAbout_Click(object? sender, EventArgs e)
    {
        // BEACON INTEGRATION: Track navigation events (dialog opens, page views, etc.)
        // This helps you understand how users navigate your application.
        _tracker.Track("navigation", "dialog_opened", new
        {
            dialog = "about"
        });

        using var dialog = new AboutDialog();
        dialog.ShowDialog(this);
    }

    private void BtnTestException_Click(object? sender, EventArgs e)
    {
        // BEACON INTEGRATION: Deliberately throw and catch an exception to verify
        // that TrackException() sends the report to the backend. In a real app you
        // would never do this — this is purely for testing the Beacon pipeline.
        try
        {
            throw new InvalidOperationException(
                "Simulated inventory sync failure — test exception for Beacon SDK demo.");
        }
        catch (Exception ex)
        {
            _tracker.TrackException(ex, ExceptionSeverity.NonFatal);

            MessageBox.Show(
                "A test exception was thrown, caught, and reported to Beacon.\n\n" +
                "Check the Beacon dashboard to see the exception report,\n" +
                "including the breadcrumb trail of your recent actions.",
                "Test Exception Sent",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void BtnTestDeadLetter_Click(object? sender, EventArgs e)
    {
        // BEACON INTEGRATION: Send an invalid event — one whose category/name
        // combination is NOT in the allowlist. If an allowlist is active for this
        // product version, the backend will reject this event and route it to the
        // dead letter queue.
        _tracker.Track("testing", "invalid_event", new
        {
            purpose = "allowlist_verification"
        });

        MessageBox.Show(
            "Sent an invalid event (category \"testing\" / name \"invalid_event\").\n\n" +
            "If an allowlist is active, this event will appear in the\n" +
            "Dead Letters page since it is not a registered event.",
            "Invalid Event Sent",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BtnExportManifest_Click(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = "json",
            FileName = "beacon-allowlist.json"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _tracker.ExportEventManifest(dialog.FileName);

            MessageBox.Show(
                $"Event manifest saved to:\n{dialog.FileName}\n\n" +
                "Upload this file to the Allowlists > Import page in the portal.",
                "Manifest Exported",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ── Consent & Reset ─────────────────────────────────────────────────

    private void BtnOptToggle_Click(object? sender, EventArgs e)
    {
        // BEACON INTEGRATION: Toggle tracking consent at runtime.
        // OptOut() stops all tracking, clears queued events, and writes a marker
        // file so the opt-out persists across app restarts. OptIn() reverses it.
        // Use this to implement a "Do not track" toggle in your settings UI.
        if (_tracker.LastFlushStatus == FlushStatus.OptedOut)
        {
            _tracker.OptIn();
            MessageBox.Show(
                "Tracking re-enabled. Events will be collected and sent.",
                "Opted In", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            _tracker.OptOut();
            MessageBox.Show(
                "Tracking disabled. No events will be collected or sent.\n" +
                "Queued events have been cleared.",
                "Opted Out", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        UpdateBeaconStatus();
    }

    private void BtnReset_Click(object? sender, EventArgs e)
    {
        // BEACON INTEGRATION: Reset clears all SDK state — actor identity,
        // session, queued events, breadcrumbs — and generates a new device ID.
        // Use this when the user logs out so the next user gets a clean slate.
        _tracker.Reset();

        MessageBox.Show(
            "Beacon state reset. Actor identity, session, and queued events cleared.\n" +
            "A new device ID has been generated.",
            "Reset Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);

        UpdateBeaconStatus();
    }

    // ── Search ─────────────────────────────────────────────────────────

    private void TxtSearch_TextChanged(object? sender, EventArgs e)
    {
        // Debounce search tracking — wait 300ms after the last keystroke
        // before sending a Track() call. This prevents flooding the SDK
        // with one event per keystroke while the user is typing.
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer?.Dispose();
        _searchDebounceTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _searchDebounceTimer.Tick += SearchDebounce_Tick;
        _searchDebounceTimer.Start();

        // Update the grid immediately (search UX should be instant)
        RefreshGrid();
    }

    private void SearchDebounce_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer?.Stop();

        var query = txtSearch.Text.Trim();
        var filteredCount = GetFilteredItems().Count;

        // BEACON INTEGRATION: Track search with query length (not the query itself,
        // to avoid PII) and the number of results. This helps you understand
        // search usage patterns without seeing what users searched for.
        _tracker.Track("inventory", "search_performed", new
        {
            query_length = query.Length,
            results_count = filteredCount
        });
    }

    // ── Grid Helpers ───────────────────────────────────────────────────

    private void RefreshGrid()
    {
        var filtered = GetFilteredItems();
        dgvItems.DataSource = null;
        dgvItems.DataSource = filtered;
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

    // ── Status Bar ─────────────────────────────────────────────────────

    private void UpdateBeaconStatus()
    {
        // BEACON INTEGRATION: Read the SDK's LastFlushStatus property to show
        // the user whether events are being delivered, queued offline, or disabled.
        // This gives users confidence that tracking is working (or explains why it isn't).
        var status = _tracker.LastFlushStatus;
        var text = status switch
        {
            FlushStatus.NotConnected => "Beacon: Starting...",
            FlushStatus.Connected => "Beacon: Connected",
            FlushStatus.Offline => "Beacon: Offline (queuing)",
            FlushStatus.Disabled => "Beacon: Disabled",
            FlushStatus.OptedOut => "Beacon: Opted Out",
            _ => "Beacon: Unknown"
        };

        if (InvokeRequired)
        {
            Invoke(() => tsslBeaconStatus.Text = text);
        }
        else
        {
            tsslBeaconStatus.Text = text;
        }
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
