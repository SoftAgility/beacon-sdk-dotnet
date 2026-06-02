namespace InventoryManager.Models;

/// <summary>
/// Simple POCO representing an inventory item. This starter app stores items
/// in memory only (no persistent storage) — the point is to give you a small,
/// real application to instrument, not to be a production inventory system.
/// </summary>
public class InventoryItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}
