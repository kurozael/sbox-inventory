using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox;

namespace Conna.Inventory;

/// <summary>
/// Base class for inventory items. Inherit this for your actual game items.
/// </summary>
public abstract class InventoryItem
{
	public static void OnNetworkedPropertySet<T>( WrappedPropertySet<T> property )
	{
		var oldValue = property.Getter();

		if ( Equals( property.Value, oldValue ) )
			return;

		if ( Connection.Local.IsHost && property.Object is InventoryItem { Inventory: not null } item )
		{
			InventorySystem.MarkDirty( item.Inventory );
			item.DirtyProperties.Add( property.MemberIdent );
		}

		property.Setter( property.Value );
	}

	public static async Task<T> InvokeOnHost<T>( WrappedMethod<Task<T>> method, params object[] args )
	{
		if ( method.Object is InventoryItem { Inventory: not null } item )
		{
			item.Caller = Rpc.Caller ?? Connection.Local;

			try
			{
				if ( Connection.Local.IsHost )
				{
					return await method.Resume();
				}

				var message = new InventoryItemInvoke( item.Inventory.InventoryId, item.Id, method.MethodIdentity, args );
				return await item.Inventory.Network.InvokeOnHostAsync<InventoryItemInvoke, T>( message );
			}
			finally
			{
				item.Caller = null;
			}
		}

		if ( method.Object is not BaseInventory inventory )
			return await method.Resume();

		{
			inventory.Caller = Rpc.Caller ?? Connection.Local;

			try
			{
				if ( Connection.Local.IsHost )
				{
					return await method.Resume();
				}

				var message = new InventoryInvoke( inventory.InventoryId, method.MethodIdentity, args );
				return await inventory.Network.InvokeOnHostAsync<InventoryInvoke, T>( message );
			}
			finally
			{
				inventory.Caller = null;
			}
		}
	}

	public static void InvokeOnHost( WrappedMethod method, params object[] args )
	{
		if ( method.Object is InventoryItem { Inventory: not null } item )
		{
			item.Caller = Rpc.Caller ?? Connection.Local;

			try
			{
				if ( Connection.Local.IsHost )
				{
					method.Resume();
					return;
				}

				var message = new InventoryItemInvoke( item.Inventory.InventoryId, item.Id, method.MethodIdentity, args );
				item.Inventory.Network.InvokeOnHost( message );
			}
			finally
			{
				item.Caller = null;
			}
		}

		if ( method.Object is not BaseInventory inventory )
		{
			method.Resume();
			return;
		}

		{
			inventory.Caller = Rpc.Caller ?? Connection.Local;

			try
			{
				if ( Connection.Local.IsHost )
				{
					method.Resume();
					return;
				}

				var message = new InventoryInvoke( inventory.InventoryId, method.MethodIdentity, args );
				inventory.Network.InvokeOnHost( message );
			}
			finally
			{
				inventory.Caller = null;
			}
		}
	}

	/// <summary>
	/// Unique identifier for this item.
	/// </summary>
	public Guid Id { get; internal set; } = Guid.NewGuid();

	/// <summary>
	/// The inventory that this item belongs to.
	/// </summary>
	public BaseInventory Inventory { get; internal set; }

	/// <summary>
	/// When inside a method with the <see cref="HostAttribute"/> this will be the <see cref="Connection"/>
	/// that called the method remotely.
	/// </summary>
	public Connection Caller { get; internal set; }

	/// <summary>
	/// Size in cells. Override for non-1x1 items.
	/// </summary>
	public virtual int Width => 1;

	public virtual int Height => 1;

	/// <summary>
	/// Maximum number of items allowed in a stack.
	/// </summary>
	public virtual int MaxStackSize => 1;

	private int _stackCount = 1;

	/// <summary>
	/// Current number of items in the stack.
	/// </summary>
	[Networked]
	public int StackCount
	{
		get => _stackCount;
		set
		{
			_stackCount = Math.Clamp( value, 0, MaxStackSize );
		}
	}

	public virtual string DisplayName => GetType().Name;
	public virtual string Category => "Default";

	/// <summary>
	/// A set of dirty networked properties that should be sent to all subscribers of the
	/// <see cref="BaseInventory"/> that this <see cref="InventoryItem"/> belongs to.
	/// </summary>
	internal readonly HashSet<int> DirtyProperties = [];

	/// <summary>
	/// Override to serialize the item for networking.
	/// </summary>
	public virtual void Serialize( Dictionary<string, object> data )
	{
		var typeDescription = TypeLibrary.GetType( GetType() );

		foreach ( var property in typeDescription.Properties.Where( p => p.HasAttribute<NetworkedAttribute>() ) )
		{
			data[property.Name] = property.GetValue( this );
		}
	}

	/// <summary>
	/// Override to deserialize an item for networking.
	/// </summary>
	public virtual void Deserialize( Dictionary<string, object> data )
	{
		var typeDescription = TypeLibrary.GetType( GetType() );

		foreach ( var property in typeDescription.Properties.Where( p => p.HasAttribute<NetworkedAttribute>() ) )
		{
			if ( data.TryGetValue( property.Name, out var value ) )
			{
				property.SetValue( this, value );
			}
		}
	}

	/// <summary>
	/// Determines whether these two items can stack together.
	/// Override to include metadata checks.
	/// </summary>
	public virtual bool CanStackWith( InventoryItem other )
	{
		if ( other is null )
			return false;

		return other.GetType() == GetType();
	}

	/// <summary>
	/// Create a new item instance that represents the same "kind" of item, but with a new stack count.
	/// Override to copy metadata fields.
	/// </summary>
	public virtual InventoryItem CreateStackClone( int stackCount )
	{
		var description = TypeLibrary.GetType( GetType() );
		var clone = description.Create<InventoryItem>();
		clone.StackCount = stackCount;
		return clone;
	}

	public virtual void OnAdded( BaseInventory baseInventory ) { }
	public virtual void OnRemoved( BaseInventory baseInventory ) { }

	/// <summary>
	/// Called when another item is dropped onto this item.
	/// Override to implement custom behaviors like container insertion or crafting.
	/// Return true to consume the interaction and prevent default stacking/swapping.
	/// </summary>
	/// <param name="droppedItem">The item being dropped onto this item.</param>
	/// <param name="inventory">The inventory where the interaction is occurring.</param>
	/// <param name="result">The result of the interaction.</param>
	/// <returns>True if the interaction was handled, false to continue with default behavior.</returns>
	public virtual bool TryInteractWith( InventoryItem droppedItem, BaseInventory inventory, out InventoryResult result )
	{
		result = InventoryResult.Success;
		return false;
	}

	/// <summary>
	/// Checks if a dropped item can potentially interact with this item.
	/// Used for UI preview validation.
	/// </summary>
	/// <param name="droppedItem">The item being dropped onto this item.</param>
	/// <param name="inventory">The inventory where the interaction would occur.</param>
	/// <returns>True if the dropped item can interact with this item.</returns>
	public virtual bool CanInteractWith( InventoryItem droppedItem, BaseInventory inventory ) => false;

	public int SpaceLeftInStack() => Math.Max( 0, MaxStackSize - StackCount );
}
