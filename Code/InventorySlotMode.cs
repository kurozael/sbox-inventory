using System;

namespace Conna.Inventory;

/// <summary>
/// Determines how items occupy space in an inventory.
/// </summary>
public enum InventorySlotMode
{
	/// <summary>
	/// Items occupy space based on their Width and Height properties (Tetris-style).
	/// </summary>
	Tetris,

	/// <summary>
	/// All items occupy exactly one slot regardless of their actual size.
	/// </summary>
	Single
}
