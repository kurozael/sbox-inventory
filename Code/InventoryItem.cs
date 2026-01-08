using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Conna.Inventory;

/// <summary>
/// Base class for inventory items. Inherit this for your actual game items.
/// </summary>
public abstract class InventoryItem
{
	private static void OnNetworkedPropertySet<T>( WrappedPropertySet<T> property )
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

	/// <summary>
	/// Unique identifier for this item.
	/// </summary>
	public Guid Id { get; internal set; } = Guid.NewGuid();

	/// <summary>
	/// The inventory that this item belongs to.
	/// </summary>
	public BaseInventory Inventory { get; internal set; }

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
