using System.Collections.Generic;
using Sandbox;

namespace Conna.Inventory.Example;

/// <summary>
/// Base class for inventory items that are based from a <see cref="GameResource"/>.
/// </summary>
public class ExampleResourceItem<T> : InventoryItem where T : ItemGameResource
{
	/// <summary>
	/// The <see cref="ItemGameResource"/> that this item is based from.
	/// </summary>
	public T Resource { get; private set; }

	public override int MaxStackSize => Resource?.MaxStackSize ?? base.MaxStackSize;
	public override string DisplayName => Resource?.DisplayName ?? base.DisplayName;
	public override string Category => Resource?.Category ?? base.Category;
	public override int Width => Resource?.Width ?? base.Width;
	public override int Height => Resource?.Height ?? base.Height;

	/// <summary>
	/// Load data from the specified <see cref="ItemGameResource"/>.
	/// </summary>
	public void LoadFromResource( T resource )
	{
		Resource = resource;
		OnResourceUpdated( resource );
	}

	/// <summary>
	/// Called when the <see cref="Resource"/> used for this item has been updated.
	/// </summary>
	protected virtual void OnResourceUpdated( T resource )
	{

	}

	public override void Serialize( Dictionary<string, object> data )
	{
		base.Serialize( data );

		data["ResourceId"] = Resource?.ResourceId ?? 0;
	}

	public override void Deserialize( Dictionary<string, object> data )
	{
		base.Deserialize( data );

		if ( !data.TryGetValue( "ResourceId", out var id ) )
			return;

		var resourceId = (int)id;
		if ( resourceId == 0 ) return;

		Resource = ResourceLibrary.Get<T>( resourceId );
		OnResourceUpdated( Resource );
	}
}
