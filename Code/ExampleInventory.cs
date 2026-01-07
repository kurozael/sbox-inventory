using System;

namespace Conna.Inventory;

/// <summary>
/// An example inventory class.
/// </summary>
public class ExampleInventory( Guid id, int width, int height, InventorySlotMode slotMode = InventorySlotMode.Tetris ) 
	: BaseInventory( id, width, height, slotMode );
