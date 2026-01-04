namespace Sandbox.Inventory;

/// <summary>
/// Where an item lives inside an inventory.
/// </summary>
public readonly record struct InventorySlot( int X, int Y, int W, int H );
