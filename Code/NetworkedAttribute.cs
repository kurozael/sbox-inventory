using System;
using Sandbox;

namespace Conna.Inventory;

/// <summary>
/// Properties belonging to an <see cref="InventoryItem"/> that have this attribute will be automatically
/// synchronized with subscribers of its parent <see cref="BaseInventory"/> whenever the value changes.
///	<p><b>The property setter must be public.</b></p>
/// </summary>
[CodeGenerator( CodeGeneratorFlags.Instance | CodeGeneratorFlags.WrapPropertySet, "Conna.Inventory.InventoryItem.OnNetworkedPropertySet" )]
[AttributeUsage( AttributeTargets.Property )]
public class NetworkedAttribute : Attribute
{

}
