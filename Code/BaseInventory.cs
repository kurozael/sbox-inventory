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

	/// <summary>
	/// Determines how items occupy space in this inventory.
	/// </summary>
	public InventorySlotMode SlotMode { get; }

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

	/// <summary>
	/// When inside a method with the <see cref="HostAttribute"/> this will be the <see cref="Connection"/>
	/// that called the method remotely.
	/// </summary>
	public Connection Caller { get; internal set; }

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

	protected BaseInventory( Guid id, int width, int height, InventorySlotMode slotMode = InventorySlotMode.Tetris )
	{
		ArgumentOutOfRangeException.ThrowIfLessThan( width, 1 );
		ArgumentOutOfRangeException.ThrowIfLessThan( height, 1 );

		Width = width;
		Height = height;
		SlotMode = slotMode;

		_chunksPerRow = (width + 63) / 64;
		_rowBits = new ulong[_chunksPerRow * height];

		InventoryId = id;
		Network = new NetworkedInventory( this );
	}

	/// <summary>
	/// Gets the effective width of an item in this inventory based on the slot mode.
	/// </summary>
	public int GetEffectiveWidth( InventoryItem item ) => SlotMode == InventorySlotMode.Single ? 1 : item.Width;

	/// <summary>
	/// Gets the effective height of an item in this inventory based on the slot mode.
	/// </summary>
	public int GetEffectiveHeight( InventoryItem item ) => SlotMode == InventorySlotMode.Single ? 1 : item.Height;

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

		var effectiveW = GetEffectiveWidth( item );
		var effectiveH = GetEffectiveHeight( item );

		if ( !IsInBounds( x, y, effectiveW, effectiveH ) )
			return false;

		if ( !CanPlaceAt( item, x, y, effectiveW, effectiveH ) )
			return false;

		var itemsAtTarget = GetItemsInRect( x, y, effectiveW, effectiveH );

		foreach ( var exclude in excludeFromCollision )
			itemsAtTarget.Remove( exclude );

		return itemsAtTarget.Count == 0;
	}

	/// <summary>
	/// Checks if an item can be transferred or swapped to the target position in another inventory.
	/// </summary>
	public bool CanTransferOrSwapTo( InventoryItem item, BaseInventory destination, int x, int y )
	{
		if ( item is null || destination is null )
			return false;

		if ( !_entries.TryGetValue( item.Id, out var entry ) )
			return false;

		// Use destination's effective size
		var effectiveW = destination.GetEffectiveWidth( item );
		var effectiveH = destination.GetEffectiveHeight( item );

		if ( !destination.IsInBounds( x, y, effectiveW, effectiveH ) )
			return false;

		if ( !destination.CanPlaceAt( item, x, y, effectiveW, effectiveH ) )
			return false;

		var itemsAtTarget = destination.GetItemsInRect( x, y, effectiveW, effectiveH );

		if ( itemsAtTarget.Count == 0 )
			return true;

		if ( itemsAtTarget.Count != 1 )
			return false;

		var other = itemsAtTarget[0];

		// Check for stacking
		if ( item.CanStackWith( other ) && other.SpaceLeftInStack() > 0 )
			return true;

		// Check if a swap is possible
		return CanSwapBetweenItems( item, other, destination );
	}

	/// <summary>
	/// Checks if two items from different inventories can be swapped.
	/// </summary>
	private bool CanSwapBetweenItems( InventoryItem itemFromThis, InventoryItem itemFromOther, BaseInventory otherInventory )
	{
		if ( !_entries.TryGetValue( itemFromThis.Id, out var entryThis ) )
			return false;

		if ( !otherInventory._entries.TryGetValue( itemFromOther.Id, out var entryOther ) )
			return false;

		var slotThis = entryThis.Slot;
		var slotOther = entryOther.Slot;

		// Calculate effective sizes for each item in each inventory
		var thisEffectiveWInOther = otherInventory.GetEffectiveWidth( itemFromThis );
		var thisEffectiveHInOther = otherInventory.GetEffectiveHeight( itemFromThis );
		var otherEffectiveWInThis = GetEffectiveWidth( itemFromOther );
		var otherEffectiveHInThis = GetEffectiveHeight( itemFromOther );

		// Check if itemFromThis can fit at itemFromOther's position
		if ( !otherInventory.IsInBounds( slotOther.X, slotOther.Y, thisEffectiveWInOther, thisEffectiveHInOther ) )
			return false;

		if ( !otherInventory.CanPlaceAt( itemFromThis, slotOther.X, slotOther.Y, thisEffectiveWInOther, thisEffectiveHInOther ) )
			return false;

		// Check if itemFromOther can fit at itemFromThis's position
		if ( IsInBounds( slotThis.X, slotThis.Y, otherEffectiveWInThis, otherEffectiveHInThis ) &&
		     CanPlaceAt( itemFromOther, slotThis.X, slotThis.Y, otherEffectiveWInThis, otherEffectiveHInThis ) )
		{
			return true;
		}

		// If the incoming item is larger than the vacated slot, check if there's any space
		if ( otherEffectiveWInThis > slotThis.W || otherEffectiveHInThis > slotThis.H )
		{
			return TryFindPlacement( itemFromOther, out _ );
		}

		// Try to find a position within the vacated space
		return TryFindSwapPositionForBetween( slotThis, otherEffectiveWInThis, otherEffectiveHInThis, out _ );
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

		var effectiveW = GetEffectiveWidth( item );
		var effectiveH = GetEffectiveHeight( item );

		if ( !IsInBounds( x, y, effectiveW, effectiveH ) )
			return false;

		if ( !CanPlaceAt( item, x, y, effectiveW, effectiveH ) )
			return false;

		var itemsAtTarget = GetItemsInRect( x, y, effectiveW, effectiveH );
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
	/// Checks if two items can be swapped without collisions.
	/// </summary>
	private bool CanSwapItems( InventoryItem itemA, InventoryItem itemB )
	{
		if ( !_entries.TryGetValue( itemA.Id, out var entryA ) )
			return false;

		if ( !_entries.TryGetValue( itemB.Id, out var entryB ) )
			return false;

		var slotA = entryA.Slot;
		var slotB = entryB.Slot;

		var effectiveWA = GetEffectiveWidth( itemA );
		var effectiveHA = GetEffectiveHeight( itemA );
		var effectiveWB = GetEffectiveWidth( itemB );
		var effectiveHB = GetEffectiveHeight( itemB );

		// Check if itemA can fit at itemB's position
		if ( !IsInBounds( slotB.X, slotB.Y, effectiveWA, effectiveHA ) )
			return false;

		if ( !CanPlaceAt( itemA, slotB.X, slotB.Y, effectiveWA, effectiveHA ) )
			return false;

		var newSlotA = slotB with
		{
			W = effectiveWA,
			H = effectiveHA
		};

		// Check if itemB can fit at itemA's origin (simple case)
		var newSlotB = slotA with
		{
			W = effectiveWB,
			H = effectiveHB
		};
		var simpleSwapWorks = IsInBounds( newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H ) &&
			CanPlaceAt( itemB, newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H ) &&
			!RectsOverlap( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H,
				newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H );

		if ( simpleSwapWorks )
		{
			// Verify no collision with other items
			return !HasSwapCollisionWithOtherItems( itemA, itemB, newSlotA, newSlotB );
		}

		// Try to find an alternative position for itemB within itemA's vacated space
		if ( !TryFindSwapPosition( slotA, newSlotA, effectiveWB, effectiveHB, out newSlotB ) )
			return false;

		if ( !CanPlaceAt( itemB, newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H ) )
			return false;

		return !HasSwapCollisionWithOtherItems( itemA, itemB, newSlotA, newSlotB );
	}

	/// <summary>
	/// Checks if the new positions for swapped items would collide with any other items in the inventory.
	/// </summary>
	private bool HasSwapCollisionWithOtherItems( InventoryItem itemA, InventoryItem itemB, InventorySlot newSlotA, InventorySlot newSlotB )
	{
		foreach ( var (inventoryItem, s) in _entries.Values )
		{
			if ( inventoryItem.Id == itemA.Id || inventoryItem.Id == itemB.Id )
				continue;

			if ( RectsOverlap( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H, s.X, s.Y, s.W, s.H ) )
				return true;

			if ( RectsOverlap( newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H, s.X, s.Y, s.W, s.H ) )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Finds a valid position for the swapped item within the vacated space, ensuring it doesn't
	/// overlap with the moving item's new position.
	/// </summary>
	private bool TryFindSwapPosition( InventorySlot vacatedSlot, InventorySlot newSlotA, int itemWidth, int itemHeight, out InventorySlot newSlotB )
	{
		// Search within the bounds of the vacated slot for a valid position
		var searchMinX = Math.Max( 0, vacatedSlot.X - itemWidth + 1 );
		var searchMaxX = Math.Min( Width - itemWidth, vacatedSlot.X + vacatedSlot.W - 1 );
		var searchMinY = Math.Max( 0, vacatedSlot.Y - itemHeight + 1 );
		var searchMaxY = Math.Min( Height - itemHeight, vacatedSlot.Y + vacatedSlot.H - 1 );

		// Prefer positions that keep the item within the vacated area, starting from the origin
		for ( var y = vacatedSlot.Y; y <= searchMaxY; y++ )
		{
			for ( var x = vacatedSlot.X; x <= searchMaxX; x++ )
			{
				if ( TrySwapPositionAt( x, y, itemWidth, itemHeight, newSlotA, out newSlotB ) )
					return true;
			}
		}

		// Try remaining positions in the extended search area
		for ( var y = searchMinY; y <= searchMaxY; y++ )
		{
			for ( var x = searchMinX; x <= searchMaxX; x++ )
			{
				// Skip positions we already checked
				if ( x >= vacatedSlot.X && y >= vacatedSlot.Y )
					continue;

				if ( TrySwapPositionAt( x, y, itemWidth, itemHeight, newSlotA, out newSlotB ) )
					return true;
			}
		}

		newSlotB = default;
		return false;
	}

	private bool TrySwapPositionAt( int x, int y, int w, int h, InventorySlot newSlotA, out InventorySlot newSlotB )
	{
		newSlotB = new InventorySlot( x, y, w, h );

		if ( !IsInBounds( x, y, w, h ) )
			return false;

		// Ensure it doesn't overlap with where the other item is going
		if ( RectsOverlap( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H, x, y, w, h ) )
			return false;

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

		if ( !CanInsertItem( item ) )
			return InventoryResult.InsertNotAllowed;

		var effectiveW = GetEffectiveWidth( item );
		var effectiveH = GetEffectiveHeight( item );

		return PlaceItem( item, new InventorySlot( x, y, effectiveW, effectiveH ) );
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

		var effectiveW = GetEffectiveWidth( item );
		var effectiveH = GetEffectiveHeight( item );

		if ( !IsInBounds( newX, newY, effectiveW, effectiveH ) )
			return InventoryResult.PlacementOutOfBounds;

		if ( !CanPlaceAt( item, newX, newY, effectiveW, effectiveH ) )
			return InventoryResult.PlacementNotAllowed;

		var oldSlot = entry.Slot;
		ClearRect( oldSlot.X, oldSlot.Y, oldSlot.W, oldSlot.H );

		if ( !IsRectFree( newX, newY, effectiveW, effectiveH ) )
		{
			FillRect( oldSlot.X, oldSlot.Y, oldSlot.W, oldSlot.H );
			return InventoryResult.PlacementCollision;
		}

		FillRect( newX, newY, effectiveW, effectiveH );
		_entries[item.Id] = new Entry( item, new InventorySlot( newX, newY, effectiveW, effectiveH ) );

		OnItemMoved?.Invoke( item, newX, newY );

		if ( IsNetworked && HasAuthority )
			Network.BroadcastItemMoved( item.Id, newX, newY );

		OnInventoryChangedInternal();

		return InventoryResult.Success;
	}

	/// <summary>
	/// Swaps the positions of two items in the same inventory.
	/// ItemA moves to itemB's position, itemB finds a valid spot in itemA's vacated space.
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

		var effectiveWA = GetEffectiveWidth( itemA );
		var effectiveHA = GetEffectiveHeight( itemA );
		var effectiveWB = GetEffectiveWidth( itemB );
		var effectiveHB = GetEffectiveHeight( itemB );

		// Where itemA wants to go (itemB's current position)
		var newSlotA = slotB with
		{
			W = effectiveWA,
			H = effectiveHA
		};

		// Check if itemA can fit at itemB's position
		if ( !IsInBounds( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H ) )
			return InventoryResult.PlacementOutOfBounds;

		if ( !CanPlaceAt( itemA, newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H ) )
			return InventoryResult.PlacementNotAllowed;

		// Try the simple case first: itemB goes to itemA's origin
		var newSlotB = slotA with
		{
			W = effectiveWB,
			H = effectiveHB
		};
		var simpleSwapWorks = IsInBounds( newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H ) &&
			CanPlaceAt( itemB, newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H ) &&
			!RectsOverlap( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H,
				newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H );

		if ( !simpleSwapWorks )
		{
			// Find a valid position for itemB within itemA's vacated space
			if ( !TryFindSwapPosition( slotA, newSlotA, effectiveWB, effectiveHB, out newSlotB ) )
				return InventoryResult.PlacementCollision;

			if ( !CanPlaceAt( itemB, newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H ) )
				return InventoryResult.PlacementNotAllowed;
		}

		// Clear both original positions
		ClearRect( slotA.X, slotA.Y, slotA.W, slotA.H );
		ClearRect( slotB.X, slotB.Y, slotB.W, slotB.H );

		// Check for collisions with other items
		foreach ( var (inventoryItem, s) in _entries.Values )
		{
			if ( inventoryItem.Id == itemA.Id || inventoryItem.Id == itemB.Id )
				continue;

			if ( RectsOverlap( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H, s.X, s.Y, s.W, s.H ) ||
			     RectsOverlap( newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H, s.X, s.Y, s.W, s.H ) )
			{
				// Restore original positions
				FillRect( slotA.X, slotA.Y, slotA.W, slotA.H );
				FillRect( slotB.X, slotB.Y, slotB.W, slotB.H );
				return InventoryResult.PlacementCollision;
			}
		}

		// Apply the swap
		FillRect( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H );
		FillRect( newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H );

		_entries[itemA.Id] = new Entry( itemA, newSlotA );
		_entries[itemB.Id] = new Entry( itemB, newSlotB );

		OnItemMoved?.Invoke( itemA, newSlotA.X, newSlotA.Y );
		OnItemMoved?.Invoke( itemB, newSlotB.X, newSlotB.Y );

		if ( IsNetworked && HasAuthority )
		{
			Network.BroadcastItemsSwapped( itemA.Id, newSlotA.X, newSlotA.Y, itemB.Id, newSlotB.X, newSlotB.Y );
		}

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

		if ( destination != this && IsNetworked && HasAuthority )
			Network.BroadcastItemRemoved( item.Id );

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

		// Use destination's effective size for the item
		var destEffectiveW = destination.GetEffectiveWidth( item );
		var destEffectiveH = destination.GetEffectiveHeight( item );

		if ( !destination.IsInBounds( x, y, destEffectiveW, destEffectiveH ) )
			return InventoryResult.PlacementOutOfBounds;

		if ( !destination.CanPlaceAt( item, x, y, destEffectiveW, destEffectiveH ) )
			return InventoryResult.PlacementNotAllowed;

		if ( !destination.IsRectFree( x, y, destEffectiveW, destEffectiveH ) )
			return InventoryResult.PlacementCollision;

		var originalSlot = sourceEntry.Slot;
		ClearRect( originalSlot.X, originalSlot.Y, originalSlot.W, originalSlot.H );
		_entries.Remove( item.Id );

		var placeResult = destination.PlaceItem( item, new InventorySlot( x, y, destEffectiveW, destEffectiveH ) );
		if ( placeResult != InventoryResult.Success )
		{
			FillRect( originalSlot.X, originalSlot.Y, originalSlot.W, originalSlot.H );
			_entries.Add( item.Id, sourceEntry );
			return placeResult;
		}

		item.OnRemoved( this );
		OnItemRemoved?.Invoke( sourceEntry );

		if ( destination != this && IsNetworked && HasAuthority )
			Network.BroadcastItemRemoved( item.Id );

		OnInventoryChangedInternal();
		return InventoryResult.Success;
	}

	/// <summary>
	/// Swaps an item from this inventory with an item in another inventory.
	/// itemFromThis moves to itemFromOther's position, itemFromOther finds a valid spot in itemFromThis's vacated space.
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

		// Calculate effective sizes for each item in each inventory
		var thisEffectiveWInOther = otherBaseInventory.GetEffectiveWidth( itemFromThis );
		var thisEffectiveHInOther = otherBaseInventory.GetEffectiveHeight( itemFromThis );
		var otherEffectiveWInThis = GetEffectiveWidth( itemFromOther );
		var otherEffectiveHInThis = GetEffectiveHeight( itemFromOther );

		// Where itemFromThis wants to go (itemFromOther's current position)
		var newSlotForThis = slotOther with
		{
			W = thisEffectiveWInOther,
			H = thisEffectiveHInOther
		};

		if ( !otherBaseInventory.IsInBounds( newSlotForThis.X, newSlotForThis.Y, newSlotForThis.W, newSlotForThis.H ) )
			return InventoryResult.PlacementOutOfBounds;

		if ( !otherBaseInventory.CanPlaceAt( itemFromThis, newSlotForThis.X, newSlotForThis.Y, newSlotForThis.W, newSlotForThis.H ) )
			return InventoryResult.PlacementNotAllowed;

		// Try the simple case first: itemFromOther goes to itemFromThis's origin
		var newSlotForOther = slotThis with
		{
			W = otherEffectiveWInThis,
			H = otherEffectiveHInThis
		};
		var simpleSwapWorks = IsInBounds( newSlotForOther.X, newSlotForOther.Y, newSlotForOther.W, newSlotForOther.H ) &&
			CanPlaceAt( itemFromOther, newSlotForOther.X, newSlotForOther.Y, newSlotForOther.W, newSlotForOther.H );

		if ( !simpleSwapWorks )
		{
			// Find a valid position for itemFromOther within itemFromThis's vacated space
			if ( !TryFindSwapPositionForBetween( slotThis, otherEffectiveWInThis, otherEffectiveHInThis, out newSlotForOther ) )
				return InventoryResult.PlacementCollision;

			if ( !CanPlaceAt( itemFromOther, newSlotForOther.X, newSlotForOther.Y, newSlotForOther.W, newSlotForOther.H ) )
				return InventoryResult.PlacementNotAllowed;
		}

		// Clear both original positions
		ClearRect( slotThis.X, slotThis.Y, slotThis.W, slotThis.H );
		_entries.Remove( itemFromThis.Id );

		otherBaseInventory.ClearRect( slotOther.X, slotOther.Y, slotOther.W, slotOther.H );
		otherBaseInventory._entries.Remove( itemFromOther.Id );

		// Check for collisions in target inventory for itemFromThis
		if ( !otherBaseInventory.IsRectFree( newSlotForThis.X, newSlotForThis.Y, newSlotForThis.W, newSlotForThis.H ) )
		{
			// Restore original positions
			FillRect( slotThis.X, slotThis.Y, slotThis.W, slotThis.H );
			_entries.Add( itemFromThis.Id, entryThis );
			otherBaseInventory.FillRect( slotOther.X, slotOther.Y, slotOther.W, slotOther.H );
			otherBaseInventory._entries.Add( itemFromOther.Id, entryOther );
			return InventoryResult.PlacementCollision;
		}

		// Check for collisions in this inventory for itemFromOther
		if ( !IsRectFree( newSlotForOther.X, newSlotForOther.Y, newSlotForOther.W, newSlotForOther.H ) )
		{
			// Restore original positions
			FillRect( slotThis.X, slotThis.Y, slotThis.W, slotThis.H );
			_entries.Add( itemFromThis.Id, entryThis );
			otherBaseInventory.FillRect( slotOther.X, slotOther.Y, slotOther.W, slotOther.H );
			otherBaseInventory._entries.Add( itemFromOther.Id, entryOther );
			return InventoryResult.PlacementCollision;
		}

		// Apply the swap
		otherBaseInventory.FillRect( newSlotForThis.X, newSlotForThis.Y, newSlotForThis.W, newSlotForThis.H );
		otherBaseInventory._entries.Add( itemFromThis.Id, new Entry( itemFromThis, newSlotForThis ) );

		FillRect( newSlotForOther.X, newSlotForOther.Y, newSlotForOther.W, newSlotForOther.H );
		_entries.Add( itemFromOther.Id, new Entry( itemFromOther, newSlotForOther ) );

		itemFromThis.Inventory = otherBaseInventory;
		itemFromOther.Inventory = this;

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

		if ( IsNetworked && HasAuthority )
		{
			Network.BroadcastItemsSwappedBetween(
				itemFromThis.Id, otherBaseInventory.InventoryId, newSlotForThis.X, newSlotForThis.Y,
				itemFromOther.Id, InventoryId, newSlotForOther.X, newSlotForOther.Y
			);
		}

		return InventoryResult.Success;
	}

	/// <summary>
	/// Finds a valid position for the swapped item within the vacated space for cross-inventory swaps.
	/// </summary>
	private bool TryFindSwapPositionForBetween( InventorySlot vacatedSlot, int itemWidth, int itemHeight, out InventorySlot newSlot )
	{
		// Search within the bounds of the vacated slot for a valid position
		var searchMinX = Math.Max( 0, vacatedSlot.X - itemWidth + 1 );
		var searchMaxX = Math.Min( Width - itemWidth, vacatedSlot.X + vacatedSlot.W - 1 );
		var searchMinY = Math.Max( 0, vacatedSlot.Y - itemHeight + 1 );
		var searchMaxY = Math.Min( Height - itemHeight, vacatedSlot.Y + vacatedSlot.H - 1 );

		// Prefer positions that keep the item within the vacated area, starting from the origin
		for ( var y = vacatedSlot.Y; y <= searchMaxY; y++ )
		{
			for ( var x = vacatedSlot.X; x <= searchMaxX; x++ )
			{
				if ( IsInBounds( x, y, itemWidth, itemHeight ) )
				{
					newSlot = new InventorySlot( x, y, itemWidth, itemHeight );
					return true;
				}
			}
		}

		// Try remaining positions in the extended search area
		for ( var y = searchMinY; y <= searchMaxY; y++ )
		{
			for ( var x = searchMinX; x <= searchMaxX; x++ )
			{
				// Skip positions we already checked
				if ( x >= vacatedSlot.X && y >= vacatedSlot.Y )
					continue;

				if ( IsInBounds( x, y, itemWidth, itemHeight ) )
				{
					newSlot = new InventorySlot( x, y, itemWidth, itemHeight );
					return true;
				}
			}
		}

		newSlot = default;
		return false;
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

		var destEffectiveW = destination.GetEffectiveWidth( item );
		var destEffectiveH = destination.GetEffectiveHeight( item );

		var itemsAtTarget = destination.GetItemsInRect( x, y, destEffectiveW, destEffectiveH );

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

		var effectiveW = GetEffectiveWidth( item );
		var effectiveH = GetEffectiveHeight( item );

		var itemsAtTarget = GetItemsInRect( x, y, effectiveW, effectiveH );
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
		Log.Info( "Try take and place: " + amount  );
		placed = null;

		// Check ownership for networked inventories
		if ( !HasAuthority )
			return InventoryResult.NoAuthority;

		var result = TryTake( item, amount, out var taken );
		if ( result != InventoryResult.Success )
			return result;

		Log.Info( "took the amount: " + amount  );

		// Adjust slot size based on slot mode
		var effectiveW = GetEffectiveWidth( taken );
		var effectiveH = GetEffectiveHeight( taken );
		var adjustedSlot = slot with
		{
			W = effectiveW,
			H = effectiveH
		};

		var placeResult = PlaceItem( taken, adjustedSlot );
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

		// Check if there's a stackable item at the target position
		var itemAtTarget = destination.GetItemAt( x, y );
		if ( itemAtTarget != null && taken.MaxStackSize > 1 && destination.CanStack( itemAtTarget, taken ) )
		{
			var spaceLeft = itemAtTarget.SpaceLeftInStack();
			if ( spaceLeft > 0 )
			{
				var amountToMove = Math.Min( taken.StackCount, spaceLeft );
				itemAtTarget.StackCount += amountToMove;
				taken.StackCount -= amountToMove;

				destination.OnInventoryChangedInternal();

				// If the entire stack was merged, we're done
				if ( taken.StackCount <= 0 )
				{
					transferred = taken;
					return InventoryResult.Success;
				}

				// Put the remainder back in the source
				RevertTake( item, taken );
				return InventoryResult.Success;
			}
		}

		// Try to swap if there's an item at target
		if ( itemAtTarget != null )
		{
			// Put the taken item back first
			RevertTake( item, taken );

			// Now try a proper swap using the full item
			var swapResult = TrySwapBetween( item, itemAtTarget, destination );
			if ( swapResult == InventoryResult.Success )
				transferred = itemAtTarget;

			return swapResult;
		}

		// No item at target, just place it
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
			.OrderByDescending( i => GetEffectiveWidth( i ) * GetEffectiveHeight( i ) )
			.ThenByDescending( i => Math.Max( GetEffectiveWidth( i ), GetEffectiveHeight( i ) ) )
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
			var effectiveW = GetEffectiveWidth( item );
			var effectiveH = GetEffectiveHeight( item );

			// Scan from top-left to bottom-right
			for ( var y = 0; y <= Height - effectiveH && !placed; y++ )
			{
				for ( var x = 0; x <= Width - effectiveW && !placed; x++ )
				{
					if ( !IsRectFree( x, y, effectiveW, effectiveH ) || !CanPlaceAt( item, x, y, effectiveW, effectiveH ) )
						continue;

					var result = PlaceItem( item, new InventorySlot( x, y, effectiveW, effectiveH ) );
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

		var effectiveW = GetEffectiveWidth( item );
		var effectiveH = GetEffectiveHeight( item );

		if ( slot.W != effectiveW || slot.H != effectiveH ) return InventoryResult.SlotSizeMismatch;
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
		{
			Network.BroadcastItemAdded( new Entry( item, slot ) );
		}

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
		var effectiveW = GetEffectiveWidth( item );
		var effectiveH = GetEffectiveHeight( item );

		for ( var y = 0; y <= Height - effectiveH; y++ )
		{
			for ( var x = 0; x <= Width - effectiveW; x++ )
			{
				if ( !IsRectFree( x, y, effectiveW, effectiveH ) || !CanPlaceAt( item, x, y, effectiveW, effectiveH ) )
					continue;

				slot = new InventorySlot( x, y, effectiveW, effectiveH );
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

	/// <summary>
	/// Forces two items to swap positions atomically. Used by networking to apply swap updates from the host.
	/// This bypasses normal collision checks since the host has already validated the swap.
	/// </summary>
	internal void ForceSwapPositions( InventoryItem itemA, int newAX, int newAY, InventoryItem itemB, int newBX, int newBY )
	{
		if ( !_entries.TryGetValue( itemA.Id, out var entryA ) || !_entries.TryGetValue( itemB.Id, out var entryB ) )
			return;

		var oldSlotA = entryA.Slot;
		var oldSlotB = entryB.Slot;

		var effectiveWA = GetEffectiveWidth( itemA );
		var effectiveHA = GetEffectiveHeight( itemA );
		var effectiveWB = GetEffectiveWidth( itemB );
		var effectiveHB = GetEffectiveHeight( itemB );

		// Clear old positions
		ClearRect( oldSlotA.X, oldSlotA.Y, oldSlotA.W, oldSlotA.H );
		ClearRect( oldSlotB.X, oldSlotB.Y, oldSlotB.W, oldSlotB.H );

		// Create new slots
		var newSlotA = new InventorySlot( newAX, newAY, effectiveWA, effectiveHA );
		var newSlotB = new InventorySlot( newBX, newBY, effectiveWB, effectiveHB );

		// Fill new positions
		FillRect( newSlotA.X, newSlotA.Y, newSlotA.W, newSlotA.H );
		FillRect( newSlotB.X, newSlotB.Y, newSlotB.W, newSlotB.H );

		// Update entries
		_entries[itemA.Id] = new Entry( itemA, newSlotA );
		_entries[itemB.Id] = new Entry( itemB, newSlotB );

		OnItemMoved?.Invoke( itemA, newSlotA.X, newSlotA.Y );
		OnItemMoved?.Invoke( itemB, newSlotB.X, newSlotB.Y );
		OnInventoryChangedInternal();
	}

	/// <summary>
	/// Forces an item to be removed from this inventory without normal checks. Used by networking.
	/// </summary>
	internal InventoryItem ForceRemoveItem( Guid itemId )
	{
		if ( !_entries.TryGetValue( itemId, out var entry ) )
			return null;

		var item = entry.Item;
		ClearRect( entry.Slot.X, entry.Slot.Y, entry.Slot.W, entry.Slot.H );
		_entries.Remove( itemId );

		item.OnRemoved( this );
		OnItemRemoved?.Invoke( entry );

		return item;
	}

	/// <summary>
	/// Forces an item to be added to this inventory at a specific position without normal checks. Used by networking.
	/// </summary>
	internal void ForceAddItem( InventoryItem item, int x, int y )
	{
		var effectiveW = GetEffectiveWidth( item );
		var effectiveH = GetEffectiveHeight( item );
		var slot = new InventorySlot( x, y, effectiveW, effectiveH );

		FillRect( slot.X, slot.Y, slot.W, slot.H );
		_entries[item.Id] = new Entry( item, slot );

		item.Inventory = this;
		item.OnAdded( this );
		OnItemAdded?.Invoke( new Entry( item, slot ) );
		OnInventoryChangedInternal();
	}

	public void Dispose()
	{
		InventorySystem.Unregister( this );
	}
}
