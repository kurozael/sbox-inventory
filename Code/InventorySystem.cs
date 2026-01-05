using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Conna.Inventory;

/// <summary>
/// A <see cref="GameObjectSystem"/> that contains all registered inventories.
/// </summary>
public class InventorySystem : GameObjectSystem<InventorySystem>
{
	private readonly Dictionary<Guid, BaseInventory> _inventories = [];
	private readonly HashSet<BaseInventory> _dirtyInventories = [];

	/// <summary>
	/// Mark an inventory as being dirty. Dirty inventories are iterated each
	/// tick to broadcast networked item properties to all subscribers.
	/// </summary>
	/// <param name="inventory"></param>
	public void MarkDirty( BaseInventory inventory )
	{
		_dirtyInventories.Add( inventory );
	}

	/// <summary>
	/// Register an inventory with this system.
	/// </summary>
	public void Register( BaseInventory baseInventory ) => _inventories.Add( baseInventory.InventoryId, baseInventory );

	/// <summary>
	/// Unregister an inventory from this system.
	/// </summary>
	public void Unregister( BaseInventory baseInventory )
	{
		_dirtyInventories.Remove( baseInventory );
		_inventories.Remove( baseInventory.InventoryId );
	}

	/// <summary>
	/// Find an inventory by its unique ID.
	/// </summary>
	public bool TryFind( Guid id, out BaseInventory baseInventory )
	{
		return _inventories.TryGetValue( id, out baseInventory );
	}

	public InventorySystem( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 0, OnUpdate, "OnUpdate" );
	}

	private TimeUntil _nextNetworkTick = 0f;

	private void OnUpdate()
	{
		// Conna: none of this is not optimized at all. I'll fix that if it becomes a problem.

		if ( !_nextNetworkTick )
			return;

		foreach ( var inventory in _dirtyInventories )
		{
			if ( inventory.Subscribers.Count == 0 || !inventory.HasAuthority )
				continue;

			var itemChangedEvents = new List<InventoryItemDataChanged>();

			foreach ( var (item, _) in inventory.Entries )
			{
				if ( item.DirtyProperties.Count == 0 )
					continue;

				var data = new Dictionary<string, object>();

				foreach ( var memberId in item.DirtyProperties )
				{
					var memberDescription = TypeLibrary.GetMemberByIdent( memberId ) as PropertyDescription;
					data[memberDescription.Name] = memberDescription.GetValue( item );
				}

				itemChangedEvents.Add( new InventoryItemDataChanged( item.Id, data ) );
				item.DirtyProperties.Clear();
			}

			if ( itemChangedEvents.Count == 0 )
				continue;

			var packet = new InventoryItemDataChangedList( itemChangedEvents.ToArray() );
			inventory.Network.SendItemDataChangedList( inventory.Subscribers, packet );

			if ( inventory.Subscribers.Contains( Connection.Local.Id ) )
				inventory.OnInventoryChanged?.Invoke();
		}

		_dirtyInventories.Clear();
		_nextNetworkTick = 0.1f;
	}
}
