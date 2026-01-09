using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox;

namespace Conna.Inventory;

/// <summary>
/// Network accessor for inventory operations. Automatically routes through the host.
/// </summary>
public class NetworkedInventory
{
	private readonly BaseInventory _inventory;
	private readonly Dictionary<Guid, TaskCompletionSource<InventoryResult>> _pendingRequests = new();
	private readonly Dictionary<Guid, TaskCompletionSource<object>> _pendingInvocations = new();
	private const int RequestTimeoutMs = 5000;

	/// <summary>
	/// Whether networking is enabled on this inventory. If networking is enabled, synchronization
	/// of the inventory is routed through the host. The host has authority over the inventory.
	/// </summary>
	public bool Enabled
	{
		get;
		set
		{
			if ( value )
				InventorySystem.Register( _inventory );
			else
				InventorySystem.Unregister( _inventory );

			field = value;
		}
	}

	/// <summary>
	/// Determines how this inventory broadcasts updates to clients.
	/// <see cref="NetworkMode.Subscribers"/> only sends to explicitly subscribed clients.
	/// <see cref="NetworkMode.Global"/> broadcasts to all connected clients.
	/// </summary>
	public NetworkMode Mode
	{
		get;
		set
		{
			if ( field == value )
				return;

			if ( Connection.Local.IsHost && value == NetworkMode.Global )
			{
				foreach ( var connection in Connection.All )
				{
					if ( connection == Connection.Local )
						continue;

					SendFullStateTo( connection.Id );
				}
			}

			field = value;
		}
	} = NetworkMode.Subscribers;

	internal NetworkedInventory( BaseInventory inventory )
	{
		_inventory = inventory;
	}

	private bool ShouldSendRequest => _inventory.IsNetworked && !_inventory.HasAuthority;

	public async Task<InventoryResult> TryMove( InventoryItem item, int newX, int newY )
	{
		if ( !ShouldSendRequest )
			return _inventory.TryMove( item, newX, newY );

		return await SendRequest( new InventoryMoveRequest( item.Id, newX, newY ) );
	}

	public async Task<InventoryResult> TryMoveOrSwap( InventoryItem item, int x, int y )
	{
		if ( ShouldSendRequest )
			return await SendRequest( new InventoryMoveRequest( item.Id, x, y ) );

		return _inventory.TryMoveOrSwap( item, x, y, out _ );
	}

	public async Task<InventoryResult> TrySwap( InventoryItem itemA, InventoryItem itemB )
	{
		if ( !ShouldSendRequest )
			return _inventory.TrySwap( itemA, itemB );

		return await SendRequest( new InventorySwapRequest( itemA.Id, itemB.Id ) );
	}

	public async Task<InventoryResult> TryTransferToAt( InventoryItem item, BaseInventory destination, int x, int y )
	{
		if ( !ShouldSendRequest )
			return _inventory.TryTransferToAt( item, destination, x, y );

		return await SendRequest( new InventoryTransferRequest( item.Id, destination.InventoryId, x, y ) );
	}

	public async Task<InventoryResult> TryTakeAndPlace( InventoryItem item, int amount, InventorySlot slot )
	{
		if ( ShouldSendRequest )
		{
			return await SendRequest( new InventoryTakeRequest( item.Id, amount, slot.X, slot.Y ) );
		}

		return _inventory.TryTakeAndPlace( item, amount, slot, out _ );
	}

	public async Task<InventoryResult> TryCombineStacks( InventoryItem source, InventoryItem dest, int amount )
	{
		if ( ShouldSendRequest )
		{
			return await SendRequest( new InventoryCombineStacksRequest( source.Id, dest.Id, amount ) );
		}

		return _inventory.TryCombineStacks( source, dest, amount, out _ );
	}

	public async Task<InventoryResult> AutoSort()
	{
		if ( !ShouldSendRequest )
			return _inventory.AutoSort();

		return await SendRequest( new InventoryAutoSortRequest() );
	}

	public async Task<InventoryResult> ConsolidateStacks()
	{
		if ( !ShouldSendRequest )
		{
			return _inventory.TryConsolidateStacks();
		}

		return await SendRequest( new InventoryConsolidateRequest() );
	}

	internal void InvokeOnHost<T>( T message ) where T : struct
	{
		SendToHost( Guid.Empty, message );
	}

	internal async Task<R> InvokeOnHostAsync<T, R>( T message ) where T : struct
	{
		var requestId = Guid.NewGuid();
		var tcs = new TaskCompletionSource<object>();
		_pendingInvocations[requestId] = tcs;

		SendToHostWithReturn<T, R>( requestId, message );

		var timeoutTask = GameTask.Delay( RequestTimeoutMs );
		var completedTask = GameTask.WhenAny( tcs.Task, timeoutTask );

		await completedTask;

		if ( completedTask != timeoutTask )
		{
			var result = await tcs.Task;

			if ( result is null )
				return default;

			return (R)result;
		}

		_pendingInvocations.Remove( requestId );
		return default;
	}

	private async Task<InventoryResult> SendRequest<T>( T message ) where T : struct
	{
		var requestId = Guid.NewGuid();
		var tcs = new TaskCompletionSource<InventoryResult>();
		_pendingRequests[requestId] = tcs;

		SendToHost( requestId, message );

		var timeoutTask = GameTask.Delay( RequestTimeoutMs );
		var completedTask = GameTask.WhenAny( tcs.Task, timeoutTask );

		await completedTask;

		if ( completedTask != timeoutTask )
			return await tcs.Task;

		_pendingRequests.Remove( requestId );
		return InventoryResult.RequestTimeout;
	}

	internal void HandleInvocationResult( Guid requestId, object result )
	{
		if ( !_pendingInvocations.TryGetValue( requestId, out var tcs ) )
			return;

		tcs.SetResult( result );
		_pendingInvocations.Remove( requestId );
	}

	internal void HandleActionResult( Guid requestId, InventoryResult result )
	{
		if ( !_pendingRequests.TryGetValue( requestId, out var tcs ) )
			return;

		tcs.SetResult( result );
		_pendingRequests.Remove( requestId );
	}

	internal void BroadcastClearAll()
	{
		if ( !Connection.Local.IsHost )
			return;

		var message = new InventoryClearAll();
		BroadcastToRecipients( message );
	}

	internal void BroadcastItemAdded( BaseInventory.Entry entry )
	{
		if ( !Connection.Local.IsHost )
			return;

		var metadata = new Dictionary<string, object>();
		entry.Item.Serialize( metadata );

		var serialized = new SerializedEntry(
			entry.Item,
			entry.Slot.X,
			entry.Slot.Y,
			entry.Slot.W,
			entry.Slot.H,
			metadata
		);

		var message = new InventoryItemAdded( serialized );
		BroadcastToRecipients( message );
	}

	internal void BroadcastItemRemoved( Guid itemId )
	{
		if ( !Connection.Local.IsHost )
			return;

		var message = new InventoryItemRemoved( itemId );
		BroadcastToRecipients( message );
	}

	internal void BroadcastItemMoved( Guid itemId, int x, int y )
	{
		if ( !Connection.Local.IsHost )
			return;

		var message = new InventoryItemMoved( itemId, x, y );
		BroadcastToRecipients( message );
	}

	internal void SendItemDataChangedList( IEnumerable<Guid> connections, InventoryItemDataChangedList list )
	{
		if ( !Connection.Local.IsHost )
			return;

		foreach ( var connection in connections )
		{
			if ( Connection.Local.Id == connection )
				continue;

			SendToConnection( connection, list );
		}
	}

	/// <summary>
	/// Broadcasts item data changes to all recipients based on the current <see cref="Mode"/>.
	/// </summary>
	internal void BroadcastItemDataChangedList( InventoryItemDataChangedList list )
	{
		if ( !Connection.Local.IsHost )
			return;

		BroadcastToRecipients( list );
	}

	public void SendFullStateTo( Guid connectionId )
	{
		if ( !Connection.Local.IsHost )
			return;

		var entries = _inventory.Entries.Select( e =>
		{
			var metadata = new Dictionary<string, object>();
			e.Item.Serialize( metadata );

			return new SerializedEntry(
				e.Item,
				e.Slot.X,
				e.Slot.Y,
				e.Slot.W,
				e.Slot.H,
				metadata
			);
		} ).ToList();

		var typeId = TypeLibrary.GetType( _inventory.GetType() ).Identity;
		var message = new InventoryStateSync( typeId, _inventory.Width, _inventory.Height, _inventory.SlotMode, entries );
		SendToConnection( connectionId, message );
	}

	internal void SendToHostWithReturn<T, R>( Guid requestId, T message ) where T : struct
	{
		if ( Connection.Local.IsHost )
			return;

		var serialized = TypeLibrary.ToBytes( message );
		ReceiveInvocationFromClient<R>( _inventory.InventoryId, requestId, serialized );
	}

	internal void SendToHost<T>( Guid requestId, T message ) where T : struct
	{
		if ( Connection.Local.IsHost )
			return;

		var serialized = TypeLibrary.ToBytes( message );
		ReceiveRequestFromClient( _inventory.InventoryId, requestId, serialized );
	}

	/// <summary>
	/// Broadcasts a message to all recipients based on the current <see cref="Mode"/>.
	/// For <see cref="NetworkMode.Subscribers"/>, only sends to subscribed clients.
	/// For <see cref="NetworkMode.Global"/>, sends to all connected clients.
	/// </summary>
	private void BroadcastToRecipients<T>( T message ) where T : struct
	{
		if ( !Connection.Local.IsHost )
			return;

		var serialized = TypeLibrary.ToBytes( message );

		if ( Mode == NetworkMode.Global )
		{
			using ( Rpc.FilterExclude( Connection.Local ) )
			{
				ReceiveMessageFromHost( _inventory.InventoryId, serialized );
			}
		}
		else
		{
			var subscribers = _inventory.Subscribers
				.Where( id => id != Connection.Local.Id )
				.Select( Connection.Find )
				.ToHashSet();

			using ( Rpc.FilterInclude( subscribers ) )
			{
				ReceiveMessageFromHost( _inventory.InventoryId, serialized );
			}
		}
	}

	private void SendToConnection<T>( Guid connectionId, T message ) where T : struct
	{
		if ( !Connection.Local.IsHost )
			return;

		var connection = Connection.Find( connectionId );
		var serialized = TypeLibrary.ToBytes( message );

		using ( Rpc.FilterInclude( connection ) )
		{
			ReceiveMessageFromHost( _inventory.InventoryId, serialized );
		}
	}

	[Rpc.Broadcast]
	private static void ReceiveMessageFromHost( Guid inventoryId, byte[] data )
	{
		var message = TypeLibrary.FromBytes<object>( data );

		BaseInventory inventory;

		if ( message is not InventoryStateSync sync )
		{
			if ( !InventorySystem.TryFind( inventoryId, out inventory ) )
				return;
		}
		else
		{
			if ( !InventorySystem.TryFind( inventoryId, out inventory ) )
			{
				var typeDescription = TypeLibrary.GetTypeByIdent( sync.TypeId );
				inventory = InventorySystem.GetOrCreate( typeDescription, inventoryId, sync.Width, sync.Height, sync.SlotMode );
				inventory.Network.Enabled = true;
			}
		}

		switch ( message )
		{
			case InventoryItemAdded msg:
				HandleItemAdded( inventory, msg );
				break;

			case InventoryItemRemoved msg:
				HandleItemRemoved( inventory, msg );
				break;

			case InventoryItemMoved msg:
				HandleItemMoved( inventory, msg );
				break;

			case InventoryItemDataChangedList msg:
				HandleItemDataChangedList( inventory, msg );
				break;

			case InventoryStateSync msg:
				HandleStateSync( inventory, msg );
				break;

			case InventoryClearAll msg:
				HandleClearAll( inventory );
				break;
		}
	}

	private static void HandleItemAdded( BaseInventory inventory, InventoryItemAdded msg )
	{
		var entry = msg.Entry;
		var item = entry.CreateItem();
		if ( item == null ) return;

		item.Id = entry.ItemId;
		item.Deserialize( entry.Metadata );

		inventory.ExecuteWithoutAuthority( () =>
		{
			inventory.TryAddAt( item, entry.X, entry.Y );
		});
	}

	private static void HandleItemRemoved( BaseInventory inventory, InventoryItemRemoved msg )
	{
		var item = inventory.Entries.FirstOrDefault( e => e.Item.Id == msg.ItemId ).Item;
		if ( item == null ) return;

		inventory.ExecuteWithoutAuthority( () =>
		{
			inventory.TryRemove( item );
		});
	}

	private static void HandleClearAll( BaseInventory inventory )
	{
		inventory.ExecuteWithoutAuthority( () =>
		{
			inventory.ClearAll();
		});
	}

	private static void HandleItemMoved( BaseInventory inventory, InventoryItemMoved msg )
	{
		var item = inventory.Entries.FirstOrDefault( e => e.Item.Id == msg.ItemId ).Item;
		if ( item == null ) return;

		inventory.ExecuteWithoutAuthority( () =>
		{
			inventory.TryMove( item, msg.X, msg.Y );
		});
	}

	private static void HandleItemDataChangedList( BaseInventory inventory, InventoryItemDataChangedList msg )
	{
		foreach ( var entry in msg.List )
		{
			var item = inventory.Entries.FirstOrDefault( e => e.Item.Id == entry.ItemId ).Item;
			item?.Deserialize( entry.Data );
		}

		inventory.OnInventoryChanged?.Invoke();
	}

	private static void HandleStateSync( BaseInventory inventory, InventoryStateSync msg )
	{
		inventory.ExecuteWithoutAuthority( () =>
		{
			var existingItems = inventory.Entries.Select( e => e.Item ).ToList();
			foreach ( var item in existingItems )
			{
				inventory.TryRemove( item );
			}

			foreach ( var entry in msg.Entries )
			{
				var item = entry.CreateItem();
				if ( item == null ) return;

				item.Id = entry.ItemId;
				item.Deserialize( entry.Metadata );

				inventory.TryAddAt( item, entry.X, entry.Y );
			}
		});
	}

	[Rpc.Host]
	private static async void ReceiveInvocationFromClient<T>( Guid inventoryId, Guid requestId, byte[] data )
	{
		var message = TypeLibrary.FromBytes<object>( data );

		if ( !InventorySystem.TryFind( inventoryId, out var inventory ) )
			return;

		if ( message is InventoryItemInvoke itemInvokeMsg )
		{
			var caller = Rpc.Caller;
			var result = await HandleInventoryItemInvoke<T>( inventory, itemInvokeMsg );

			if ( requestId == Guid.Empty )
				return;

			using ( Rpc.FilterInclude( caller ) )
			{
				ReceiveInvocationResult( inventoryId, requestId, result );
			}

			return;
		}

		if ( message is not InventoryInvoke inventoryInvokeMsg )
			return;

		{
			var result = HandleInventoryInvoke<T>( inventory, inventoryInvokeMsg );

			if ( requestId == Guid.Empty )
				return;

			using ( Rpc.FilterInclude( Rpc.Caller ) )
			{
				ReceiveInvocationResult( inventoryId, requestId, result );
			}
		}
	}

	[Rpc.Host]
	private static void ReceiveRequestFromClient( Guid inventoryId, Guid requestId, byte[] data )
	{
		var message = TypeLibrary.FromBytes<object>( data );

		if ( !InventorySystem.TryFind( inventoryId, out var inventory ) )
			return;

		if ( inventory.Network.Mode == NetworkMode.Subscribers && !inventory.Subscribers.Contains( Rpc.CallerId ) )
			return;

		{
			var result = message switch
			{
				InventoryMoveRequest msg => HandleMoveRequest( inventory, msg ),
				InventorySwapRequest msg => HandleSwapRequest( inventory, msg ),
				InventoryTransferRequest msg => HandleTransferRequest( inventory, msg ),
				InventoryTakeRequest msg => HandleTakeRequest( inventory, msg ),
				InventoryCombineStacksRequest msg => HandleCombineStacksRequest( inventory, msg ),
				InventoryAutoSortRequest msg => HandleAutoSortRequest( inventory, msg ),
				InventoryConsolidateRequest msg => HandleConsolidateRequest( inventory, msg ),
				_ => InventoryResult.InsertNotAllowed
			};

			using ( Rpc.FilterInclude( Rpc.Caller ) )
			{
				ReceiveActionResult( inventoryId, requestId, result );
			}
		}
	}

	private static Task<T> HandleInventoryItemInvoke<T>( BaseInventory inventory, InventoryItemInvoke msg )
	{
		var item = inventory.Entries.FirstOrDefault( e => e.Item.Id == msg.ItemId ).Item;
		var method = TypeLibrary.GetMemberByIdent( msg.MethodId ) as MethodDescription;

		item.Caller = Rpc.Caller;

		try
		{
			if ( method.ReturnType == typeof( Task<T> ) )
			{
				return method.InvokeWithReturn<Task<T>>( item, msg.Args );
			}

			method.Invoke( item, msg.Args );
			return null;
		}
		finally
		{
			item.Caller = null;
		}
	}

	private static Task<T> HandleInventoryInvoke<T>( BaseInventory inventory, InventoryInvoke msg )
	{
		var method = TypeLibrary.GetMemberByIdent( msg.MethodId ) as MethodDescription;

		inventory.Caller = Rpc.Caller;

		try
		{
			if ( method.ReturnType.IsAssignableTo( typeof( Task<> ) ) )
			{
				return method.InvokeWithReturn<Task<T>>( inventory, msg.Args );
			}

			method.Invoke( inventory, msg.Args );
			return null;
		}
		finally
		{
			inventory.Caller = null;
		}
	}

	private static InventoryResult HandleMoveRequest( BaseInventory inventory, InventoryMoveRequest msg )
	{
		var item = inventory.Entries.FirstOrDefault( e => e.Item.Id == msg.ItemId ).Item;
		return item == null ? InventoryResult.ItemNotInInventory : inventory.TryMoveOrSwap( item, msg.X, msg.Y, out _ );
	}

	private static InventoryResult HandleSwapRequest( BaseInventory inventory, InventorySwapRequest msg )
	{
		var itemA = inventory.Entries.FirstOrDefault( e => e.Item.Id == msg.ItemAId ).Item;
		var itemB = inventory.Entries.FirstOrDefault( e => e.Item.Id == msg.ItemBId ).Item;
		if ( itemA == null || itemB == null ) return InventoryResult.ItemNotInInventory;

		return inventory.TrySwap( itemA, itemB );
	}

	private static InventoryResult HandleTransferRequest( BaseInventory inventory, InventoryTransferRequest msg )
	{
		if ( !InventorySystem.TryFind( msg.DestinationId, out var destination ) )
			return InventoryResult.DestinationWasNull;

		var item = inventory.Entries.FirstOrDefault( e => e.Item.Id == msg.ItemId ).Item;
		return item == null ? InventoryResult.ItemNotInInventory : inventory.TryTransferToAt( item, destination, msg.X, msg.Y );
	}

	private static InventoryResult HandleTakeRequest( BaseInventory inventory, InventoryTakeRequest msg )
	{
		var item = inventory.Entries.FirstOrDefault( e => e.Item.Id == msg.ItemId ).Item;
		return item == null ? InventoryResult.ItemNotInInventory : inventory.TryTakeAndPlace( item, msg.Amount, new InventorySlot( msg.X, msg.Y, item.Width, item.Height ), out _ );
	}

	private static InventoryResult HandleCombineStacksRequest( BaseInventory inventory, InventoryCombineStacksRequest msg )
	{
		var source = inventory.Entries.FirstOrDefault( e => e.Item.Id == msg.SourceId ).Item;
		var dest = inventory.Entries.FirstOrDefault( e => e.Item.Id == msg.DestId ).Item;
		if ( source == null || dest == null ) return InventoryResult.ItemNotInInventory;

		return inventory.TryCombineStacks( source, dest, msg.Amount, out _ );
	}

	private static InventoryResult HandleAutoSortRequest( BaseInventory inventory, InventoryAutoSortRequest msg )
	{
		return inventory.AutoSort();
	}

	private static InventoryResult HandleConsolidateRequest( BaseInventory inventory, InventoryConsolidateRequest msg )
	{
		return inventory.TryConsolidateStacks();
	}

	[Rpc.Broadcast]
	private static void ReceiveInvocationResult( Guid inventoryId, Guid requestId, object result )
	{
		if ( InventorySystem.TryFind( inventoryId, out var inventory ) )
		{
			inventory.Network.HandleInvocationResult( requestId, result );
		}
	}

	[Rpc.Broadcast]
	private static void ReceiveActionResult( Guid inventoryId, Guid requestId, InventoryResult result )
	{
		if ( InventorySystem.TryFind( inventoryId, out var inventory ) )
		{
			inventory.Network.HandleActionResult( requestId, result );
		}
	}
}

public struct InventoryMoveRequest
{
	public Guid ItemId;
	public int X;
	public int Y;

	public InventoryMoveRequest( Guid itemId, int x, int y )
	{
		ItemId = itemId;
		X = x;
		Y = y;
	}
}

public struct InventorySwapRequest
{
	public Guid ItemAId;
	public Guid ItemBId;

	public InventorySwapRequest( Guid itemAId, Guid itemBId )
	{
		ItemAId = itemAId;
		ItemBId = itemBId;
	}
}

public struct InventoryTransferRequest
{
	public Guid ItemId;
	public Guid DestinationId;
	public int X;
	public int Y;

	public InventoryTransferRequest( Guid itemId, Guid destinationId, int x, int y )
	{
		ItemId = itemId;
		DestinationId = destinationId;
		X = x;
		Y = y;
	}
}

public struct InventoryTakeRequest
{
	public Guid ItemId;
	public int Amount;
	public int X;
	public int Y;

	public InventoryTakeRequest( Guid itemId, int amount, int x, int y )
	{
		ItemId = itemId;
		Amount = amount;
		X = x;
		Y = y;
	}
}

public struct InventoryCombineStacksRequest
{
	public Guid SourceId;
	public Guid DestId;
	public int Amount;

	public InventoryCombineStacksRequest( Guid sourceId, Guid destId, int amount )
	{
		SourceId = sourceId;
		DestId = destId;
		Amount = amount;
	}
}

public struct InventoryAutoSortRequest
{

}

public struct InventoryConsolidateRequest
{

}

public struct InventoryClearAll
{

}

public struct InventoryItemAdded
{
	public SerializedEntry Entry;

	public InventoryItemAdded( SerializedEntry entry )
	{
		Entry = entry;
	}
}

public struct InventoryItemRemoved
{
	public Guid ItemId;

	public InventoryItemRemoved( Guid itemId )
	{
		ItemId = itemId;
	}
}

public struct InventoryItemMoved
{
	public Guid ItemId;
	public int X;
	public int Y;

	public InventoryItemMoved( Guid itemId, int x, int y )
	{
		ItemId = itemId;
		X = x;
		Y = y;
	}
}

public struct InventoryItemDataChanged
{
	public Guid ItemId;
	public Dictionary<string, object> Data;

	public InventoryItemDataChanged( Guid itemId, Dictionary<string, object> data )
	{
		ItemId = itemId;
		Data = data;
	}
}

public struct InventoryItemDataChangedList
{
	public InventoryItemDataChanged[] List;

	public InventoryItemDataChangedList( InventoryItemDataChanged[] list )
	{
		List = list;
	}
}

public struct InventoryItemInvoke
{
	public Guid InventoryId;
	public Guid ItemId;
	public int MethodId;
	public object[] Args;

	public InventoryItemInvoke( Guid inventoryId, Guid itemId, int methodId, params object[] args )
	{
		InventoryId = inventoryId;
		ItemId = itemId;
		MethodId = methodId;
		Args = args;
	}
}

public struct InventoryInvoke
{
	public Guid InventoryId;
	public int MethodId;
	public object[] Args;

	public InventoryInvoke( Guid inventoryId, int methodId, params object[] args )
	{
		InventoryId = inventoryId;
		MethodId = methodId;
		Args = args;
	}
}

public struct InventoryStateSync
{
	public List<SerializedEntry> Entries;
	public InventorySlotMode SlotMode;
	public int Height;
	public int Width;
	public int TypeId;

	public InventoryStateSync( int typeId, int width, int height, InventorySlotMode slotMode, List<SerializedEntry> entries )
	{
		SlotMode = slotMode;
		Entries = entries;
		Width = width;
		Height = height;
		TypeId = typeId;
	}
}

public struct SerializedEntry
{
	public Guid ItemId;
	public int[] GenericTypeIds;
	public int TypeId;
	public int X;
	public int Y;
	public int W;
	public int H;
	public Dictionary<string, object> Metadata;

	public SerializedEntry( InventoryItem item, int x, int y, int w, int h, Dictionary<string, object> metadata )
	{
		var itemType = item.GetType();
		var typeDescription = TypeLibrary.GetType( itemType );

		if ( typeDescription.IsGenericType )
		{
			GenericTypeIds = TypeLibrary.GetGenericArguments( itemType )
				.Select( t => TypeLibrary.GetType( t ).Identity )
				.ToArray();
		}

		ItemId = item.Id;
		TypeId = typeDescription.Identity;
		X = x;
		Y = y;
		W = w;
		H = h;
		Metadata = metadata;
	}

	/// <summary>
	/// Create an <see cref="InventoryItem"/> from this serialized entry.
	/// </summary>
	public InventoryItem CreateItem()
	{
		var itemType = TypeLibrary.GetTypeByIdent( TypeId );

		InventoryItem item;

		if ( itemType.IsGenericType )
		{
			var genericTypes = GenericTypeIds.Select( id => TypeLibrary.GetTypeByIdent( id ).TargetType ).ToArray();
			item = itemType.CreateGeneric<InventoryItem>( genericTypes );
		}
		else
		{
			item = itemType.Create<InventoryItem>();
		}

		return item;
	}
}
