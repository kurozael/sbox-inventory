namespace Conna.Inventory;

/// <summary>
/// Defines how an inventory broadcasts network updates to clients.
/// </summary>
public enum NetworkMode
{
	/// <summary>
	/// Only clients that have been explicitly subscribed via <see cref="BaseInventory.AddSubscriber"/>
	/// will receive inventory updates. Best for player-specific inventories where not everyone
	/// needs to see the contents.
	/// </summary>
	Subscribers,

	/// <summary>
	/// All connected clients automatically receive inventory updates.
	/// Best for shared world inventories that everyone can see.
	/// </summary>
	Global
}
