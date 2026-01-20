using Sandbox;

namespace Conna.Inventory;

/// <summary>
/// A base item asset class that inherits <see cref="GameResource"/>. Inherit this for any
/// further customization needed for game resource items.
/// </summary>
public abstract class ItemGameResource : GameResource
{
	[Property] public string DisplayName { get; set; }
	[Property] public string Category { get; set; }
	[Property] public int MaxStackSize { get; set; } = 1;
	[Property] public int Width { get; set; } = 1;
	[Property] public int Height { get; set; } = 1;
}
