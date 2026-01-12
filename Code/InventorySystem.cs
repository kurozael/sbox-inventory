using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Conna.Inventory;

/// <summary>
/// A <see cref="GameObjectSystem"/> that contains all registered inventories.
/// </summary>
public class InventorySystem : GameObjectSystem<InventorySystem>, Component.INetworkSnapshot
{
	private readonly Dictionary<Guid, BaseInventory> _inventories = [];
	private readonly HashSet<BaseInventory> _dirtyInventories = [];

	/// <summary>
	/// Get an inventory by its unique ID, or create a new one and register it for networking if one cannot be found. Inventories
	/// created this way will be networked by default.
	/// </summary>
	public static T GetOrCreate<T>( Guid id, int width, int height, InventorySlotMode slotMode = InventorySlotMode.Tetris ) where T : BaseInventory
	{
		var typeDescription = TypeLibrary.GetType<T>();
		return GetOrCreate( typeDescription, id, width, height, slotMode ) as T;
	}

	/// <summary>
	/// Get an inventory by its unique ID, or create a new one and register it for networking if one cannot be found. Inventories
	/// created this way will be networked by default.
	/// </summary>
	public static BaseInventory GetOrCreate( TypeDescription typeDescription, Guid id, int width, int height, InventorySlotMode slotMode = InventorySlotMode.Tetris )
	{
		if ( TryFind( id, out var inventory ) )
			return inventory;

		// This is a bit shitty, but we'll try to find a constructor that takes the width and height parameters.
		// If we can't find one, we'll fall back to a constructor that takes only the ID.
		try
		{
			inventory = typeDescription.Create<BaseInventory>( [id, width, height, slotMode] );
		}
		catch ( Exception )
		{
			inventory = typeDescription.Create<BaseInventory>( [id] );
		}

		inventory.Network.Enabled = true;
		return inventory;
	}

	/// <summary>
	/// Remove an inventory from the system and dispose of it.
	/// </summary>
	public static void Remove( BaseInventory inventory )
	{
		var system = Current;
		if ( system is null ) return;

		if ( system._inventories.Remove( inventory.InventoryId ) )
			inventory.Dispose();

		system._dirtyInventories.Remove( inventory );
	}

	void Component.INetworkSnapshot.ReadSnapshot( ref ByteStream bs )
	{
		var count = bs.Read<int>();

		for ( var i = 0; i < count; i++ )
		{
			var inventoryId = bs.Read<Guid>();
			var data = bs.ReadArray<byte>( 1024 * 1024 * 16 );
			var state = TypeLibrary.FromBytes<InventoryStateSync>( data );

			if ( !TryFind( inventoryId, out var inventory ) )
			{
				var typeDescription = TypeLibrary.GetTypeByIdent( state.TypeId );
				inventory = GetOrCreate( typeDescription, inventoryId, state.Width, state.Height, state.SlotMode );
				inventory.Network.Enabled = true;
			}

			inventory.Network.UpdateState( state );
		}
	}

	void Component.INetworkSnapshot.WriteSnapshot( ref ByteStream bs )
	{
		var inventories = _inventories.Values.Where( x => x.Network.Mode == NetworkMode.Global ).ToArray();

		bs.Write( inventories.Length );

		foreach ( var inventory in inventories )
		{
			var state = inventory.Network.SerializeState();
			var serialized = TypeLibrary.ToBytes( state );
			bs.Write( inventory.InventoryId );
			bs.WriteArray( serialized );
		}
	}

	/// <summary>
	/// Mark an inventory as being dirty. Dirty inventories are iterated each
	/// tick to broadcast networked item properties to all subscribers.
	/// </summary>
	/// <param name="inventory"></param>
	public static void MarkDirty( BaseInventory inventory )
	{
		var system = Current;
		if ( system is null ) return;

		system._dirtyInventories.Add( inventory );
	}

	/// <summary>
	/// Register an inventory with this system.
	/// </summary>
	public static void Register( BaseInventory baseInventory )
	{
		var system = Current;
		if ( system is null ) return;

		if ( !system._inventories.TryAdd( baseInventory.InventoryId, baseInventory ) )
			return;

		if ( baseInventory.Network.Mode == NetworkMode.Subscribers )
			return;

		if ( !Connection.Local.IsHost )
			return;

		foreach ( var connection in Connection.All )
		{
			if ( connection == Connection.Local )
				continue;

			baseInventory.Network.SendFullStateTo( connection.Id );
		}
	}

	/// <summary>
	/// Unregister an inventory from this system.
	/// </summary>
	public static void Unregister( BaseInventory baseInventory )
	{
		var system = Current;
		if ( system is null ) return;

		system._dirtyInventories.Remove( baseInventory );
		system._inventories.Remove( baseInventory.InventoryId );
	}

	/// <summary>
	/// Find an inventory by its unique ID.
	/// </summary>
	public static bool TryFind( Guid id, out BaseInventory inventory )
	{
		var system = Current;
		if ( system is not null )
		{
			return system._inventories.TryGetValue( id, out inventory );
		}

		inventory = null;
		return false;

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
			if ( !inventory.HasAuthority )
				continue;

			// For subscriber mode, skip if no subscribers
			// For global mode, we always broadcast (if there are any clients)
			if ( inventory.Network.Mode == NetworkMode.Subscribers && inventory.Subscribers.Count == 0 )
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

			if ( inventory.Network.Mode == NetworkMode.Global )
			{
				// Broadcast to all clients
				inventory.Network.BroadcastItemDataChangedList( packet );
			}
			else
			{
				// Send the data only to subscribers
				inventory.Network.SendItemDataChangedList( inventory.Subscribers, packet );
			}

			// Trigger local change event if we're a subscriber (or global mode)
			if ( inventory.Network.Mode == NetworkMode.Global || inventory.Subscribers.Contains( Connection.Local.Id ) )
				inventory.OnInventoryChanged?.Invoke();
		}

		_dirtyInventories.Clear();
		_nextNetworkTick = 0.1f;
	}
}
