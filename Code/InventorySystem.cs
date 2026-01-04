using System;
using System.Collections.Generic;
using Sandbox;

namespace Conna.Inventory;

/// <summary>
/// A <see cref="GameObjectSystem"/> that contains all registered inventories.
/// </summary>
public class InventorySystem : GameObjectSystem<InventorySystem>
{
	private readonly Dictionary<Guid, BaseInventory> _inventories = new();

	/// <summary>
	/// Register an inventory with this system.
	/// </summary>
	public void Register( BaseInventory baseInventory ) => _inventories.Add( baseInventory.InventoryId, baseInventory );

	/// <summary>
	/// Unregister an inventory from this system.
	/// </summary>
	public void Unregister( BaseInventory baseInventory ) => _inventories.Remove( baseInventory.InventoryId );

	/// <summary>
	/// Find an inventory by its unique ID.
	/// </summary>
	public bool TryFind( Guid id, out BaseInventory baseInventory )
	{
		return _inventories.TryGetValue( id, out baseInventory );
	}

	public InventorySystem( Scene scene ) : base( scene )
	{

	}
}
