namespace BeaconExample.InventoryManager.Models;

/// <summary>
/// Simple POCO representing an inventory item. This example app stores items
/// in memory only (no persistent storage) — the focus is on demonstrating
/// Beacon SDK integration, not inventory management.
/// </summary>
public class InventoryItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}
