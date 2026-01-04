using System;
using System.Collections.Generic;

namespace Conna.Inventory;

/// <summary>
/// Base class for inventory items. Inherit this for your actual game items.
/// </summary>
public abstract class InventoryItem
{
	public Guid Id { get; internal set; } = Guid.NewGuid();

	/// <summary>
	/// Size in cells. Override for non-1x1 items.
	/// </summary>
	public virtual int Width => 1;

	public virtual int Height => 1;

	/// <summary>
	/// Maximum number of items allowed in a stack.
	/// </summary>
	public virtual int MaxStackSize => 1;

	/// <summary>
	/// Current number of items in the stack.
	/// </summary>
	public int StackCount
	{
		get;
		private set
		{
			if ( field == value )
				return;

			field = value;
			MarkDirty();
		}
	} = 1;

	public virtual string DisplayName => GetType().Name;
	public virtual string Category => "Default";

	// Dirty tracking for networking
	internal bool IsDirty { get; private set; }

	protected void MarkDirty()
	{
		IsDirty = true;
	}

	internal void ClearDirty()
	{
		IsDirty = false;
	}

	/// <summary>
	/// Override to serialize custom item data for networking.
	/// Called when an item is dirty or needs full sync.
	/// </summary>
	public virtual void SerializeMetadata( Dictionary<string, object> data )
	{
		data["StackCount"] = StackCount;
	}

	/// <summary>
	/// Override to deserialize custom item data from network.
	/// </summary>
	public virtual void DeserializeMetadata( Dictionary<string, object> data )
	{
		if ( data.TryGetValue( "StackCount", out var stackCount ) )
		{
			StackCount = (int)stackCount;
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
		var obj = description.Create<InventoryItem>();
		obj.SetStackCount( stackCount );
		return obj;
	}

	public virtual void OnAdded( BaseInventory baseInventory ) { }
	public virtual void OnRemoved( BaseInventory baseInventory ) { }

	public void SetStackCount( int value )
	{
		ArgumentOutOfRangeException.ThrowIfLessThan( value, 1 );

		if ( value > MaxStackSize )
			throw new ArgumentOutOfRangeException( nameof( value ), $"StackCount cannot exceed MaxStackSize ({MaxStackSize})." );

		StackCount = value;
	}

	public int SpaceLeftInStack() => Math.Max( 0, MaxStackSize - StackCount );

	internal void AddToStackUnsafe( int amount ) => StackCount += amount;
	internal void RemoveFromStackUnsafe( int amount ) => StackCount -= amount;
}
