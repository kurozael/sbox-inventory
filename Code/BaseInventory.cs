using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Conna.Inventory;

/// <summary>
/// Base grid inventory with tetris-style packing and stacking. Inherit to implement special rules.
/// </summary>
public abstract class BaseInventory : IDisposable
{
	public readonly record struct Entry( InventoryItem Item, InventorySlot Slot );

	/// <summary>
	///	The width of the inventory in grid cells.
	/// </summary>
	public int Width { get; }

	/// <summary>
	/// The height of the inventory in grid cells.
	/// </summary>
	public int Height { get; }

	private bool _bypassAuthorityCheck;
	private readonly int _chunksPerRow;
	private readonly ulong[] _rowBits;
	private readonly Dictionary<Guid, Entry> _entries = new();

	public IReadOnlyCollection<Entry> Entries => _entries.Values;

	/// <summary>
	/// Unique identifier of the inventory.
	/// </summary>
	public Guid InventoryId { get; }

	/// <summary>
	/// Returns true if the inventory is currently being synced across the network.
	/// </summary>
	public bool IsNetworked => Network.Enabled;

	/// <summary>
	/// Access to the networked inventory. You can make networked changes to the inventory using this object.
	/// </summary>
	public NetworkedInventory Network { get; }

	private readonly HashSet<Guid> _subscribers = [];

	/// <summary>
	/// Check if we can modify this inventory directly.
	/// </summary>
	public bool HasAuthority
	{
		get
		{
			if ( _bypassAuthorityCheck )
				return true;

			// If not networked, always allow
			if ( !IsNetworked )
				return true;

			// If networked but no active network session, allow (failsafe)
			if ( !Networking.IsActive )
				return true;

			// If networked and we're the host, allow
			return Connection.Local?.IsHost ?? false;
		}
	}

	/// <summary>
	/// Adds a new subscriber to the inventory using the provided connection identifier. If the inventory is networked, has authority, and
	/// the connection is not the local one, it sends the full state of the inventory to the specified subscriber.
	/// </summary>
	public void AddSubscriber( Guid connectionId )
	{
		if ( !_subscribers.Add( connectionId ) )
			return;

		if ( IsNetworked && HasAuthority && Connection.Local.Id != connectionId )
		{
			Network.SendFullStateTo( connectionId );
		}
	}

	/// <summary>
	/// Removes a subscriber from the inventory.
	/// </summary>
	public void RemoveSubscriber( Guid connectionId )
	{
		_subscribers.Remove( connectionId );
	}

	public IReadOnlySet<Guid> Subscribers => _subscribers;

	/// <summary>
	/// Called when an item is added to the inventory.
	/// </summary>
	public Action<Entry> OnItemAdded;

	/// <summary>
	/// Called when an item is removed from the inventory.
	/// </summary>
	public Action<Entry> OnItemRemoved;

	/// <summary>
	/// Called when the inventory is changed in some way.
	/// </summary>
	public Action OnInventoryChanged;

	/// <summary>
	/// Called when an item is moved within the inventory.
	/// </summary>
	public Action<InventoryItem, int, int> OnItemMoved;

	protected BaseInventory( Guid id, int width, int height )
	{
		ArgumentOutOfRangeException.ThrowIfLessThan( width, 1 );
		ArgumentOutOfRangeException.ThrowIfLessThan( height, 1 );

		Width = width;
		Height = height;

		_chunksPerRow = (width + 63) / 64;
		_rowBits = new ulong[_chunksPerRow * height];

		InventoryId = id;
		Network = new NetworkedInventory( this );
	}

	protected virtual bool CanInsertItem( InventoryItem item ) => true;
	protected virtual bool CanRemoveItem( InventoryItem item ) => true;
	protected virtual bool CanTransferItemTo( InventoryItem item, BaseInventory destination ) => true;
	protected virtual bool CanReceiveTransferFrom( InventoryItem item, BaseInventory source ) => true;
	protected virtual bool CanPlaceAt( InventoryItem item, int x, int y, int w, int h ) => true;
	protected virtual bool CanStack( InventoryItem a, InventoryItem b ) => a.CanStackWith( b );

	public bool Contains( InventoryItem item ) => item is not null && _entries.ContainsKey( item.Id );

	/// <summary>
	/// Attempts to retrieve the inventory slot associated with the specified inventory item.
	/// </summary>
	public bool TryGetSlot( InventoryItem item, out InventorySlot slot )
	{
		slot = default;

		if ( item is null )
			return false;

		if ( !_entries.TryGetValue( item.Id, out var entry ) )
			return false;

		slot = entry.Slot;
		return true;

	}

	/// <summary>
	/// Retrieves the inventory item located at the specified grid coordinates within the inventory.
	/// If no item is found at the given coordinates, the method returns null.
	/// </summary>
	public InventoryItem GetItemAt( int x, int y )
	{
		foreach ( var (inventoryItem, slot) in _entries.Values )
		{
			if ( x >= slot.X && x < slot.X + slot.W && y >= slot.Y && y < slot.Y + slot.H )
				return inventoryItem;
		}

		return null;
	}

	/// <summary>
	/// Retrieves all inventory items within the specified rectangular area.
	/// </summary>
	public List<InventoryItem> GetItemsInRect( int x, int y, int w, int h )
	{
		var result = new List<InventoryItem>();

		foreach ( var (inventoryItem, slot) in _entries.Values )
		{
			if ( x < slot.X + slot.W && x + w > slot.X && y < slot.Y + slot.H && y + h > slot.Y )
				result.Add( inventoryItem );
		}

		return result;
	}

	/// <summary>
	/// Merges stackable items to consolidate space.
	/// </summary>
	public InventoryResult TryConsolidateStacks()
	{
		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		var allItems = _entries.Values.Select( e => e.Item ).Where( i => i.MaxStackSize > 1 ).ToList();

		for ( var i = 0; i < allItems.Count; i++ )
		{
			var sourceItem = allItems[i];
			if ( sourceItem.StackCount <= 0 || !Contains( sourceItem ) )
				continue;

			for ( var j = 0; j < allItems.Count; j++ )
			{
				if ( i == j )
					continue;

				var destinationItem = allItems[j];
				if ( !Contains( destinationItem ) )
					continue;

				if ( !CanStack( destinationItem, sourceItem ) )
					continue;

				var spaceLeft = destinationItem.SpaceLeftInStack();
				if ( spaceLeft <= 0 )
					continue;

				var amountToMove = Math.Min( sourceItem.StackCount, spaceLeft );
				destinationItem.StackCount += amountToMove;
				sourceItem.StackCount -= amountToMove;

				if ( sourceItem.StackCount > 0 )
					continue;

				TryRemove( sourceItem );
				break;
			}
		}

		OnInventoryChangedInternal();
		return InventoryResult.Success;
	}

	/// <summary>
	/// Checks if an item can be placed at the specified position, optionally excluding certain items from collision checks.
	/// </summary>
	public bool CanPlaceItemAt( InventoryItem item, int x, int y, params InventoryItem[] excludeFromCollision )
	{
		if ( item is null )
			return false;

		if ( !IsInBounds( x, y, item.Width, item.Height ) )
			return false;

		if ( !CanPlaceAt( item, x, y, item.Width, item.Height ) )
			return false;

		var itemsAtTarget = GetItemsInRect( x, y, item.Width, item.Height );

		foreach ( var exclude in excludeFromCollision )
			itemsAtTarget.Remove( exclude );

		return itemsAtTarget.Count == 0;
	}

	/// <summary>
	/// Checks if an item can be moved or swapped to the target position.
	/// </summary>
	public bool CanMoveOrSwap( InventoryItem item, int x, int y )
	{
		if ( item is null )
			return false;

		if ( !_entries.TryGetValue( item.Id, out var entry ) )
			return false;

		if ( !IsInBounds( x, y, item.Width, item.Height ) )
			return false;

		if ( !CanPlaceAt( item, x, y, item.Width, item.Height ) )
			return false;

		var itemsAtTarget = GetItemsInRect( x, y, item.Width, item.Height );
		itemsAtTarget.Remove( item );

		if ( itemsAtTarget.Count == 0 )
			return true;

		if ( itemsAtTarget.Count == 1 )
		{
			var other = itemsAtTarget[0];

			if ( item.CanStackWith( other ) && other.SpaceLeftInStack() > 0 )
				return true;

			return CanSwapItems( item, other );
		}

		return false;
	}

	/// <summary>
	/// Checks if two items can be swapped (each fits in the other's position).
	/// </summary>
	private bool CanSwapItems( InventoryItem itemA, InventoryItem itemB )
	{
		if ( !_entries.TryGetValue( itemA.Id, out var entryA ) )
			return false;

		if ( !_entries.TryGetValue( itemB.Id, out var entryB ) )
			return false;

		var slotA = entryA.Slot;
		var slotB = entryB.Slot;

		if ( !IsInBounds( slotB.X, slotB.Y, itemA.Width, itemA.Height ) )
			return false;

		if ( !IsInBounds( slotA.X, slotA.Y, itemB.Width, itemB.Height ) )
			return false;

		if ( !CanPlaceAt( itemA, slotB.X, slotB.Y, itemA.Width, itemA.Height ) )
			return false;

		if ( !CanPlaceAt( itemB, slotA.X, slotA.Y, itemB.Width, itemB.Height ) )
			return false;

		var newSlotA = new InventorySlot( slotB.X, slotB.Y, itemA.Width, itemA.Height );
		var newSlotB = new InventorySlot( slotA.X, slotA.Y, itemB.Width, itemB.Height );

		var aOverlapsB = RectsOverlap( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H, newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H );

		if ( !aOverlapsB )
		{
			foreach ( var (inventoryItem, s) in _entries.Values )
			{
				if ( inventoryItem.Id == itemA.Id || inventoryItem.Id == itemB.Id )
					continue;

				if ( RectsOverlap( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H, s.X, s.Y, s.W, s.H ) )
					return false;

				if ( RectsOverlap( newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H, s.X, s.Y, s.W, s.H ) )
					return false;
			}
		}
		else
		{
			return false;
		}

		return true;
	}

	private static bool RectsOverlap( int x1, int y1, int w1, int h1, int x2, int y2, int w2, int h2 )
	{
		return x1 < x2 + w2 && x1 + w1 > x2 && y1 < y2 + h2 && y1 + h1 > y2;
	}

	public InventoryResult TryAdd( InventoryItem item )
	{
		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( item is null )
			return InventoryResult.ItemWasNull;

		if ( Contains( item ) )
			return InventoryResult.ItemAlreadyInInventory;

		if ( !CanInsertItem( item ) )
			return InventoryResult.InsertNotAllowed;

		if ( item.StackCount <= 0 || item.StackCount > item.MaxStackSize )
			return InventoryResult.InvalidStackCount;

		if ( item.MaxStackSize > 1 && item.StackCount > 0 )
			MergeIntoExistingStacks( item );

		while ( item.StackCount > 0 )
		{
			var placeCount = Math.Min( item.StackCount, item.MaxStackSize );

			InventoryItem toPlace;
			if ( placeCount == item.StackCount )
			{
				toPlace = item;
			}
			else
			{
				toPlace = item.CreateStackClone( placeCount );
				item.StackCount -= placeCount;
			}

			if ( !TryFindPlacement( toPlace, out var slot ) )
				return InventoryResult.NoSpaceAvailable;

			var result = PlaceItem( toPlace, slot );
			if ( result != InventoryResult.Success )
				return result;

			if ( ReferenceEquals( toPlace, item ) )
				break;
		}

		return InventoryResult.Success;
	}

	/// <summary>
	/// Adds an item at a specific position. Does not merge into existing stacks.
	/// </summary>
	public InventoryResult TryAddAt( InventoryItem item, int x, int y )
	{
		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( item is null )
			return InventoryResult.ItemWasNull;

		if ( Contains( item ) )
			return InventoryResult.ItemAlreadyInInventory;

		return !CanInsertItem( item ) ? InventoryResult.InsertNotAllowed : PlaceItem( item, new InventorySlot( x, y, item.Width, item.Height ) );
	}

	public InventoryResult TryRemove( InventoryItem item )
	{
		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( item is null )
			return InventoryResult.ItemWasNull;

		if ( !_entries.TryGetValue( item.Id, out var entry ) )
			return InventoryResult.ItemNotInInventory;

		if ( !CanRemoveItem( item ) )
			return InventoryResult.RemoveNotAllowed;

		ClearRect( entry.Slot.X, entry.Slot.Y, entry.Slot.W, entry.Slot.H );
		_entries.Remove( item.Id );

		item.Inventory = null;
		item.DirtyProperties.Clear();

		item.OnRemoved( this );
		OnItemRemoved?.Invoke( entry );

		if ( IsNetworked && HasAuthority )
			Network.BroadcastItemRemoved( item.Id );

		OnInventoryChangedInternal();
		return InventoryResult.Success;
	}

	/// <summary>
	/// Moves an item to a new position within the same inventory.
	/// </summary>
	public InventoryResult TryMove( InventoryItem item, int newX, int newY )
	{
		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( item is null )
			return InventoryResult.ItemWasNull;

		if ( !_entries.TryGetValue( item.Id, out var entry ) )
			return InventoryResult.ItemNotInInventory;

		if ( !IsInBounds( newX, newY, item.Width, item.Height ) )
			return InventoryResult.PlacementOutOfBounds;

		if ( !CanPlaceAt( item, newX, newY, item.Width, item.Height ) )
			return InventoryResult.PlacementNotAllowed;

		var oldSlot = entry.Slot;
		ClearRect( oldSlot.X, oldSlot.Y, oldSlot.W, oldSlot.H );

		if ( !IsRectFree( newX, newY, item.Width, item.Height ) )
		{
			FillRect( oldSlot.X, oldSlot.Y, oldSlot.W, oldSlot.H );
			return InventoryResult.PlacementCollision;
		}

		FillRect( newX, newY, item.Width, item.Height );
		_entries[item.Id] = new Entry( item, new InventorySlot( newX, newY, item.Width, item.Height ) );

		OnItemMoved?.Invoke( item, newX, newY );

		if ( IsNetworked && HasAuthority )
			Network.BroadcastItemMoved( item.Id, newX, newY );

		OnInventoryChangedInternal();

		return InventoryResult.Success;
	}

	/// <summary>
	/// Swaps the positions of two items in the same inventory.
	/// </summary>
	public InventoryResult TrySwap( InventoryItem itemA, InventoryItem itemB )
	{
		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( itemA is null || itemB is null )
			return InventoryResult.ItemWasNull;

		if ( itemA.Id == itemB.Id )
			return InventoryResult.CannotCombineWithSelf;

		if ( !_entries.TryGetValue( itemA.Id, out var entryA ) || !_entries.TryGetValue( itemB.Id, out var entryB ) )
			return InventoryResult.ItemNotInInventory;

		var slotA = entryA.Slot;
		var slotB = entryB.Slot;

		var newSlotA = new InventorySlot( slotB.X, slotB.Y, itemA.Width, itemA.Height );
		var newSlotB = new InventorySlot( slotA.X, slotA.Y, itemB.Width, itemB.Height );

		if ( !IsInBounds( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H ) ||
		     !IsInBounds( newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H ) )
			return InventoryResult.PlacementOutOfBounds;

		if ( !CanPlaceAt( itemA, newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H ) ||
		     !CanPlaceAt( itemB, newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H ) )
			return InventoryResult.PlacementNotAllowed;

		if ( RectsOverlap( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H, newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H ) )
			return InventoryResult.PlacementCollision;

		ClearRect( slotA.X, slotA.Y, slotA.W, slotA.H );
		ClearRect( slotB.X, slotB.Y, slotB.W, slotB.H );

		foreach ( var (inventoryItem, s) in _entries.Values )
		{
			if ( inventoryItem.Id == itemA.Id || inventoryItem.Id == itemB.Id )
				continue;

			if ( !RectsOverlap( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H, s.X, s.Y, s.W, s.H ) &&
			     !RectsOverlap( newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H, s.X, s.Y, s.W, s.H ) )
			{
				continue;
			}

			FillRect( slotA.X, slotA.Y, slotA.W, slotA.H );
			FillRect( slotB.X, slotB.Y, slotB.W, slotB.H );
			return InventoryResult.PlacementCollision;
		}

		FillRect( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H );
		FillRect( newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H );

		_entries[itemA.Id] = new Entry( itemA, newSlotA );
		_entries[itemB.Id] = new Entry( itemB, newSlotB );

		OnInventoryChangedInternal();
		return InventoryResult.Success;
	}

	/// <summary>
	/// Transfers an item to another inventory, placing it at the first available position.
	/// </summary>
	public InventoryResult TryTransferTo( InventoryItem item, BaseInventory destination )
	{
		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( destination is null )
			return InventoryResult.DestinationWasNull;

		if ( item is null )
			return InventoryResult.ItemWasNull;

		if ( !_entries.TryGetValue( item.Id, out var sourceEntry ) )
			return InventoryResult.ItemNotInInventory;

		if ( !CanRemoveItem( item ) )
			return InventoryResult.RemoveNotAllowed;

		if ( !CanTransferItemTo( item, destination ) )
			return InventoryResult.TransferNotAllowed;

		if ( !destination.CanReceiveTransferFrom( item, this ) )
			return InventoryResult.ReceiveNotAllowed;

		var originalSlot = sourceEntry.Slot;
		ClearRect( originalSlot.X, originalSlot.Y, originalSlot.W, originalSlot.H );
		_entries.Remove( item.Id );

		var addResult = destination.TryAdd( item );
		if ( addResult != InventoryResult.Success )
		{
			FillRect( originalSlot.X, originalSlot.Y, originalSlot.W, originalSlot.H );
			_entries.Add( item.Id, sourceEntry );
			return addResult;
		}

		item.OnRemoved( this );
		OnItemRemoved?.Invoke( sourceEntry );

		OnInventoryChangedInternal();
		return InventoryResult.Success;
	}

	/// <summary>
	/// Transfers an item to another inventory at a specific position.
	/// </summary>
	public InventoryResult TryTransferToAt( InventoryItem item, BaseInventory destination, int x, int y )
	{
		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( destination is null )
			return InventoryResult.DestinationWasNull;

		if ( item is null )
			return InventoryResult.ItemWasNull;

		if ( !_entries.TryGetValue( item.Id, out var sourceEntry ) )
			return InventoryResult.ItemNotInInventory;

		if ( !CanRemoveItem( item ) )
			return InventoryResult.RemoveNotAllowed;

		if ( !CanTransferItemTo( item, destination ) )
			return InventoryResult.TransferNotAllowed;

		if ( !destination.CanReceiveTransferFrom( item, this ) )
			return InventoryResult.ReceiveNotAllowed;

		if ( !destination.CanInsertItem( item ) )
			return InventoryResult.InsertNotAllowed;

		if ( !destination.IsInBounds( x, y, item.Width, item.Height ) )
			return InventoryResult.PlacementOutOfBounds;

		if ( !destination.CanPlaceAt( item, x, y, item.Width, item.Height ) )
			return InventoryResult.PlacementNotAllowed;

		if ( !destination.IsRectFree( x, y, item.Width, item.Height ) )
			return InventoryResult.PlacementCollision;

		var originalSlot = sourceEntry.Slot;
		ClearRect( originalSlot.X, originalSlot.Y, originalSlot.W, originalSlot.H );
		_entries.Remove( item.Id );

		var placeResult = destination.PlaceItem( item, new InventorySlot( x, y, item.Width, item.Height ) );
		if ( placeResult != InventoryResult.Success )
		{
			FillRect( originalSlot.X, originalSlot.Y, originalSlot.W, originalSlot.H );
			_entries.Add( item.Id, sourceEntry );
			return placeResult;
		}

		item.OnRemoved( this );
		OnItemRemoved?.Invoke( sourceEntry );

		OnInventoryChangedInternal();

		return InventoryResult.Success;
	}

	/// <summary>
	/// Swaps an item from this inventory with an item in another inventory.
	/// Each item ends up at the other's original position.
	/// </summary>
	public InventoryResult TrySwapBetween( InventoryItem itemFromThis, InventoryItem itemFromOther, BaseInventory otherBaseInventory )
	{
		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( otherBaseInventory is null )
			return InventoryResult.DestinationWasNull;

		if ( itemFromThis is null || itemFromOther is null )
			return InventoryResult.ItemWasNull;

		if ( !_entries.TryGetValue( itemFromThis.Id, out var entryThis ) )
			return InventoryResult.ItemNotInInventory;

		if ( !otherBaseInventory._entries.TryGetValue( itemFromOther.Id, out var entryOther ) )
			return InventoryResult.ItemNotInInventory;

		if ( !CanRemoveItem( itemFromThis ) )
			return InventoryResult.RemoveNotAllowed;

		if ( !otherBaseInventory.CanRemoveItem( itemFromOther ) )
			return InventoryResult.RemoveNotAllowed;

		if ( !CanTransferItemTo( itemFromThis, otherBaseInventory ) )
			return InventoryResult.TransferNotAllowed;

		if ( !otherBaseInventory.CanTransferItemTo( itemFromOther, this ) )
			return InventoryResult.TransferNotAllowed;

		if ( !otherBaseInventory.CanReceiveTransferFrom( itemFromThis, this ) )
			return InventoryResult.ReceiveNotAllowed;

		if ( !CanReceiveTransferFrom( itemFromOther, otherBaseInventory ) )
			return InventoryResult.ReceiveNotAllowed;

		var slotThis = entryThis.Slot;
		var slotOther = entryOther.Slot;

		if ( !otherBaseInventory.CanPlaceAt( itemFromThis, slotOther.X, slotOther.Y, itemFromThis.Width, itemFromThis.Height ) )
			return InventoryResult.PlacementNotAllowed;

		if ( !CanPlaceAt( itemFromOther, slotThis.X, slotThis.Y, itemFromOther.Width, itemFromOther.Height ) )
			return InventoryResult.PlacementNotAllowed;

		if ( !otherBaseInventory.IsInBounds( slotOther.X, slotOther.Y, itemFromThis.Width, itemFromThis.Height ) )
			return InventoryResult.PlacementOutOfBounds;

		if ( !IsInBounds( slotThis.X, slotThis.Y, itemFromOther.Width, itemFromOther.Height ) )
			return InventoryResult.PlacementOutOfBounds;

		ClearRect( slotThis.X, slotThis.Y, slotThis.W, slotThis.H );
		_entries.Remove( itemFromThis.Id );

		otherBaseInventory.ClearRect( slotOther.X, slotOther.Y, slotOther.W, slotOther.H );
		otherBaseInventory._entries.Remove( itemFromOther.Id );

		var newSlotForThis = new InventorySlot( slotOther.X, slotOther.Y, itemFromThis.Width, itemFromThis.Height );
		var newSlotForOther = new InventorySlot( slotThis.X, slotThis.Y, itemFromOther.Width, itemFromOther.Height );

		if ( !otherBaseInventory.IsRectFree( newSlotForThis.X, newSlotForThis.Y, newSlotForThis.W, newSlotForThis.H ) ||
		     !IsRectFree( newSlotForOther.X, newSlotForOther.Y, newSlotForOther.W, newSlotForOther.H ) )
		{
			FillRect( slotThis.X, slotThis.Y, slotThis.W, slotThis.H );
			_entries.Add( itemFromThis.Id, entryThis );
			otherBaseInventory.FillRect( slotOther.X, slotOther.Y, slotOther.W, slotOther.H );
			otherBaseInventory._entries.Add( itemFromOther.Id, entryOther );
			return InventoryResult.PlacementCollision;
		}

		otherBaseInventory.FillRect( newSlotForThis.X, newSlotForThis.Y, newSlotForThis.W, newSlotForThis.H );
		otherBaseInventory._entries.Add( itemFromThis.Id, new Entry( itemFromThis, newSlotForThis ) );

		FillRect( newSlotForOther.X, newSlotForOther.Y, newSlotForOther.W, newSlotForOther.H );
		_entries.Add( itemFromOther.Id, new Entry( itemFromOther, newSlotForOther ) );

		itemFromThis.OnRemoved( this );
		itemFromThis.OnAdded( otherBaseInventory );
		itemFromOther.OnRemoved( otherBaseInventory );
		itemFromOther.OnAdded( this );

		OnItemRemoved?.Invoke( entryThis );
		OnItemAdded?.Invoke( new Entry( itemFromOther, newSlotForOther ) );
		OnInventoryChangedInternal();

		otherBaseInventory.OnItemRemoved?.Invoke( entryOther );
		otherBaseInventory.OnItemAdded?.Invoke( new Entry( itemFromThis, newSlotForThis ) );
		otherBaseInventory.OnInventoryChangedInternal();

		return InventoryResult.Success;
	}

	/// <summary>
	/// Transfers an item to another inventory, swapping with any single item at the target position.
	/// If the target is empty, just transfers. If there's exactly one item, swaps.
	/// </summary>
	public InventoryResult TryTransferOrSwapAt( InventoryItem item, BaseInventory destination, int x, int y, out InventoryItem swappedItem )
	{
		swappedItem = null;

		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( destination is null )
			return InventoryResult.DestinationWasNull;

		if ( item is null )
			return InventoryResult.ItemWasNull;

		if ( !_entries.TryGetValue( item.Id, out _ ) )
			return InventoryResult.ItemNotInInventory;

		var itemsAtTarget = destination.GetItemsInRect( x, y, item.Width, item.Height );

		if ( itemsAtTarget.Count == 0 )
			return TryTransferToAt( item, destination, x, y );

		if ( itemsAtTarget.Count != 1 )
			return InventoryResult.PlacementCollision;

		var targetItem = itemsAtTarget[0];

		if ( item.CanStackWith( targetItem ) && targetItem.SpaceLeftInStack() > 0 )
		{
			var result = TryTransferTo( item, destination );
			if ( result == InventoryResult.Success )
				return result;
		}

		var swapResult = TrySwapBetween( item, targetItem, destination );
		if ( swapResult == InventoryResult.Success )
			swappedItem = targetItem;

		return swapResult;

	}

	/// <summary>
	/// Moves an item within the same inventory, or swaps if there's exactly one item at the target.
	/// </summary>
	public InventoryResult TryMoveOrSwap( InventoryItem item, int x, int y, out InventoryItem swappedItem )
	{
		swappedItem = null;

		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( item is null )
			return InventoryResult.ItemWasNull;

		if ( !_entries.TryGetValue( item.Id, out _ ) )
			return InventoryResult.ItemNotInInventory;

		var itemsAtTarget = GetItemsInRect( x, y, item.Width, item.Height );
		itemsAtTarget.Remove( item );

		if ( itemsAtTarget.Count == 0 )
			return TryMove( item, x, y );

		if ( itemsAtTarget.Count != 1 )
			return InventoryResult.PlacementCollision;

		var targetItem = itemsAtTarget[0];

		if ( item.CanStackWith( targetItem ) && targetItem.SpaceLeftInStack() > 0 )
		{
			var combineResult = TryCombineStacks( item, targetItem, -1, out _ );
			if ( combineResult == InventoryResult.Success )
				return combineResult;
		}

		var swapResult = TrySwap( item, targetItem );
		if ( swapResult == InventoryResult.Success )
			swappedItem = targetItem;

		return swapResult;
	}

	public InventoryResult TryTake( InventoryItem item, int amount, out InventoryItem taken )
	{
		taken = null;

		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( item is null )
			return InventoryResult.ItemWasNull;

		if ( !_entries.TryGetValue( item.Id, out _ ) )
			return InventoryResult.ItemNotInInventory;

		if ( !CanRemoveItem( item ) )
			return InventoryResult.RemoveNotAllowed;

		if ( amount <= 0 )
			return InventoryResult.AmountMustBePositive;

		if ( amount > item.StackCount )
			return InventoryResult.AmountExceedsStack;

		if ( amount == item.StackCount )
		{
			var removeResult = TryRemove( item );
			if ( removeResult != InventoryResult.Success )
				return removeResult;

			taken = item;
			return InventoryResult.Success;
		}

		if ( item.MaxStackSize <= 1 )
			return InventoryResult.ItemNotStackable;

		taken = item.CreateStackClone( amount );
		item.StackCount -= amount;

		OnInventoryChangedInternal();
		return InventoryResult.Success;
	}

	public InventoryResult TryTakeAndPlace( InventoryItem item, int amount, InventorySlot slot, out InventoryItem placed )
	{
		placed = null;

		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		var result = TryTake( item, amount, out var taken );
		if ( result != InventoryResult.Success )
			return result;

		var placeResult = PlaceItem( taken, slot );
		if ( placeResult != InventoryResult.Success )
		{
			RevertTake( item, taken );
			return placeResult;
		}

		placed = taken;
		return InventoryResult.Success;
	}

	public InventoryResult TryTakeAndTransferTo( InventoryItem item, int amount, BaseInventory destination, out InventoryItem transferred )
	{
		transferred = null;

		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( destination is null )
			return InventoryResult.DestinationWasNull;

		var result = TryTake( item, amount, out var taken );
		if ( result != InventoryResult.Success )
			return result;

		if ( !CanTransferItemTo( taken, destination ) || !destination.CanReceiveTransferFrom( taken, this ) )
		{
			RevertTake( item, taken );
			return InventoryResult.TransferNotAllowed;
		}

		var transferResult = destination.TryAdd( taken );
		if ( transferResult != InventoryResult.Success )
		{
			RevertTake( item, taken );
			return transferResult;
		}

		transferred = taken;
		return InventoryResult.Success;
	}

	public InventoryResult TryTakeAndTransferToAt( InventoryItem item, int amount, BaseInventory destination, int x, int y, out InventoryItem transferred )
	{
		transferred = null;

		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( destination is null )
			return InventoryResult.DestinationWasNull;

		var result = TryTake( item, amount, out var taken );
		if ( result != InventoryResult.Success )
			return result;

		if ( !CanTransferItemTo( taken, destination ) || !destination.CanReceiveTransferFrom( taken, this ) )
		{
			RevertTake( item, taken );
			return InventoryResult.TransferNotAllowed;
		}

		var transferResult = destination.TryAddAt( taken, x, y );
		if ( transferResult != InventoryResult.Success )
		{
			RevertTake( item, taken );
			return transferResult;
		}

		transferred = taken;
		return InventoryResult.Success;
	}

	public InventoryResult TrySplitAndPlace( InventoryItem item, int splitAmount, InventorySlot slot, out InventoryItem placed )
		=> TryTakeAndPlace( item, splitAmount, slot, out placed );

	public InventoryResult TrySplitAndTransferTo( InventoryItem item, int splitAmount, BaseInventory destination, out InventoryItem transferred )
		=> TryTakeAndTransferTo( item, splitAmount, destination, out transferred );

	public InventoryResult TrySplitAndTransferToAt( InventoryItem item, int splitAmount, BaseInventory destination, int x, int y, out InventoryItem transferred )
		=> TryTakeAndTransferToAt( item, splitAmount, destination, x, y, out transferred );

	public InventoryResult TryCombineStacks( InventoryItem source, InventoryItem destination, int amount, out int moved )
	{
		moved = 0;

		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( source is null || destination is null )
			return InventoryResult.ItemWasNull;

		if ( source.Id == destination.Id )
			return InventoryResult.CannotCombineWithSelf;

		if ( !_entries.ContainsKey( source.Id ) || !_entries.ContainsKey( destination.Id ) )
			return InventoryResult.BothItemsMustBeInInventory;

		if ( source.MaxStackSize <= 1 || destination.MaxStackSize <= 1 )
			return InventoryResult.ItemNotStackable;

		if ( !CanStack( destination, source ) )
			return InventoryResult.StackingNotAllowed;

		var maxMove = Math.Min( source.StackCount, destination.SpaceLeftInStack() );
		if ( maxMove <= 0 )
			return InventoryResult.DestinationStackFull;

		amount = amount <= 0 ? maxMove : Math.Min( amount, maxMove );

		destination.StackCount += amount;
		source.StackCount -= amount;
		moved = amount;

		if ( source.StackCount <= 0 )
			TryRemove( source );

		OnInventoryChangedInternal();
		return InventoryResult.Success;
	}

	/// <summary>
	/// Combines stacks across inventories. Source is in this inventory, dest is in another.
	/// </summary>
	public InventoryResult TryCombineStacksTo( InventoryItem source, InventoryItem destination, BaseInventory otherBaseInventory, int amount, out int moved )
	{
		moved = 0;

		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		if ( otherBaseInventory is null )
			return InventoryResult.DestinationWasNull;

		if ( source is null || destination is null )
			return InventoryResult.ItemWasNull;

		if ( source.Id == destination.Id )
			return InventoryResult.CannotCombineWithSelf;

		if ( !_entries.ContainsKey( source.Id ) )
			return InventoryResult.ItemNotInInventory;

		if ( !otherBaseInventory._entries.ContainsKey( destination.Id ) )
			return InventoryResult.ItemNotInInventory;

		if ( source.MaxStackSize <= 1 || destination.MaxStackSize <= 1 )
			return InventoryResult.ItemNotStackable;

		if ( !CanStack( destination, source ) )
			return InventoryResult.StackingNotAllowed;

		var maxMove = Math.Min( source.StackCount, destination.SpaceLeftInStack() );
		if ( maxMove <= 0 )
			return InventoryResult.DestinationStackFull;

		amount = amount <= 0 ? maxMove : Math.Min( amount, maxMove );

		destination.StackCount += amount;
		source.StackCount -= amount;
		moved = amount;

		if ( source.StackCount <= 0 )
			TryRemove( source );

		OnInventoryChangedInternal();
		otherBaseInventory.OnInventoryChangedInternal();

		return InventoryResult.Success;
	}

	public InventoryResult AutoSort()
	{
		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		// Collect all items and sort by size (largest first)
		var items = _entries.Values
			.Select( e => e.Item )
			.OrderByDescending( i => i.Width * i.Height )
			.ThenByDescending( i => Math.Max( i.Width, i.Height ) )
			.ToList();

		foreach ( var item in items )
		{
			if ( !CanInsertItem( item ) )
				return InventoryResult.InsertNotAllowed;
		}

		// Clear the inventory
		ClearAll();

		// Try to place each item, starting from top-left
		foreach ( var item in items )
		{
			var placed = false;

			// Scan from top-left to bottom-right
			for ( var y = 0; y <= Height - item.Height && !placed; y++ )
			{
				for ( var x = 0; x <= Width - item.Width && !placed; x++ )
				{
					if ( !IsRectFree( x, y, item.Width, item.Height ) || !CanPlaceAt( item, x, y, item.Width, item.Height ) )
						continue;

					var result = PlaceItem( item, new InventorySlot( x, y, item.Width, item.Height ) );
					if ( result == InventoryResult.Success )
					{
						placed = true;
					}
				}
			}
		}

		OnInventoryChangedInternal();
		return InventoryResult.Success;
	}

	private void OnInventoryChangedInternal()
	{
		OnInventoryChanged?.Invoke();
	}

	private void MergeIntoExistingStacks( InventoryItem incoming )
	{
		var candidates = _entries.Values
			.Where( e => e.Item.MaxStackSize > 1 && e.Item.StackCount < e.Item.MaxStackSize && CanStack( e.Item, incoming ) )
			.OrderBy( e => e.Slot.Y )
			.ThenBy( e => e.Slot.X )
			.ToList();

		foreach ( var candidate in candidates )
		{
			if ( incoming.StackCount <= 0 )
				return;

			var destination = candidate.Item;
			var move = Math.Min( incoming.StackCount, destination.SpaceLeftInStack() );
			if ( move <= 0 )
				continue;

			destination.StackCount += move;
			incoming.StackCount -= move;
			OnInventoryChangedInternal();
		}
	}

	private InventoryResult PlaceItem( InventoryItem item, InventorySlot slot )
	{
		if ( item is null ) return InventoryResult.ItemWasNull;
		if ( Contains( item ) ) return InventoryResult.ItemAlreadyInInventory;
		if ( !CanInsertItem( item ) ) return InventoryResult.InsertNotAllowed;
		if ( slot.W != item.Width || slot.H != item.Height ) return InventoryResult.SlotSizeMismatch;
		if ( !IsInBounds( slot.X, slot.Y, slot.W, slot.H ) ) return InventoryResult.PlacementOutOfBounds;
		if ( !IsRectFree( slot.X, slot.Y, slot.W, slot.H ) ) return InventoryResult.PlacementCollision;
		if ( !CanPlaceAt( item, slot.X, slot.Y, slot.W, slot.H ) ) return InventoryResult.PlacementNotAllowed;
		if ( item.StackCount <= 0 || item.StackCount > item.MaxStackSize ) return InventoryResult.InvalidStackCount;

		FillRect( slot.X, slot.Y, slot.W, slot.H );
		_entries.Add( item.Id, new Entry( item, slot ) );

		item.Inventory = this;
		item.DirtyProperties.Clear();

		item.OnAdded( this );
		OnItemAdded?.Invoke( new Entry( item, slot ) );

		if ( IsNetworked && HasAuthority )
			Network.BroadcastItemAdded( new Entry( item, slot ) );

		OnInventoryChangedInternal();
		return InventoryResult.Success;
	}

	private void RevertTake( InventoryItem original, InventoryItem taken )
	{
		if ( original is null || taken is null ) return;

		if ( Contains( original ) )
		{
			original.StackCount += taken.StackCount;
			OnInventoryChangedInternal();
		}
		else
		{
			TryAdd( original );
		}
	}

	private bool TryFindPlacement( InventoryItem item, out InventorySlot slot )
	{
		for ( var y = 0; y <= Height - item.Height; y++ )
		{
			for ( var x = 0; x <= Width - item.Width; x++ )
			{
				if ( !IsRectFree( x, y, item.Width, item.Height ) || !CanPlaceAt( item, x, y, item.Width, item.Height ) )
					continue;

				slot = new InventorySlot( x, y, item.Width, item.Height );
				return true;
			}
		}

		slot = default;
		return false;
	}

	/// <summary>
	/// Clear all items from the inventory.
	/// </summary>
	public InventoryResult ClearAll()
	{
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		_entries.Clear();
		Array.Clear( _rowBits );

		if ( IsNetworked && HasAuthority )
			Network.BroadcastClearAll();

		OnInventoryChangedInternal();
		return InventoryResult.Success;
	}

	private bool IsInBounds( int x, int y, int w, int h ) => x >= 0 && y >= 0 && x + w <= Width && y + h <= Height;
	private int RowOffset( int y ) => y * _chunksPerRow;

	private bool IsRectFree( int x, int y, int w, int h )
	{
		for ( var row = y; row < y + h; row++ )
			if ( RowIntersects( row, x, w ) ) return false;
		return true;
	}

	private bool RowIntersects( int row, int x, int w )
	{
		var endBit = x + w;
		var startChunk = x / 64;
		var endChunk = (endBit - 1) / 64;
		var rowBase = RowOffset( row );

		for ( var chunk = startChunk; chunk <= endChunk; chunk++ )
		{
			var chunkStart = chunk * 64;
			var lo = Math.Max( x, chunkStart ) - chunkStart;
			var hi = Math.Min( endBit, chunkStart + 64 ) - chunkStart;
			var mask = (hi - lo) == 64 ? ulong.MaxValue : ((1UL << (hi - lo)) - 1UL) << lo;
			if ( (_rowBits[rowBase + chunk] & mask) != 0 ) return true;
		}
		return false;
	}

	private void FillRect( int x, int y, int w, int h )
	{
		for ( var row = y; row < y + h; row++ ) RowOr( row, x, w );
	}

	private void ClearRect( int x, int y, int w, int h )
	{
		for ( var row = y; row < y + h; row++ ) RowAndNot( row, x, w );
	}

	private void RowOr( int row, int x, int w )
	{
		var endBit = x + w;
		var startChunk = x / 64;
		var endChunk = (endBit - 1) / 64;
		var rowBase = RowOffset( row );

		for ( var chunk = startChunk; chunk <= endChunk; chunk++ )
		{
			var chunkStart = chunk * 64;
			var lo = Math.Max( x, chunkStart ) - chunkStart;
			var hi = Math.Min( endBit, chunkStart + 64 ) - chunkStart;
			var mask = (hi - lo) == 64 ? ulong.MaxValue : ((1UL << (hi - lo)) - 1UL) << lo;
			_rowBits[rowBase + chunk] |= mask;
		}
	}

	private void RowAndNot( int row, int x, int w )
	{
		var endBit = x + w;
		var startChunk = x / 64;
		var endChunk = (endBit - 1) / 64;
		var rowBase = RowOffset( row );

		for ( var chunk = startChunk; chunk <= endChunk; chunk++ )
		{
			var chunkStart = chunk * 64;
			var lo = Math.Max( x, chunkStart ) - chunkStart;
			var hi = Math.Min( endBit, chunkStart + 64 ) - chunkStart;
			var mask = (hi - lo) == 64 ? ulong.MaxValue : ((1UL << (hi - lo)) - 1UL) << lo;
			_rowBits[rowBase + chunk] &= ~mask;
		}
	}

	/// <summary>
	/// Execute an action with authority checks bypassed.
	/// </summary>
	internal InventoryResult ExecuteWithoutAuthority( Func<InventoryResult> action )
	{
		_bypassAuthorityCheck = true;

		try
		{
			return action();
		}
		finally
		{
			_bypassAuthorityCheck = false;
		}
	}

	/// <summary>
	/// Execute an action with authority checks bypassed.
	/// </summary>
	internal void ExecuteWithoutAuthority( Action action )
	{
		_bypassAuthorityCheck = true;

		try
		{
			action();
		}
		finally
		{
			_bypassAuthorityCheck = false;
		}
	}

	public void Dispose()
	{
		InventorySystem.Current?.Unregister( this );
	}
}
