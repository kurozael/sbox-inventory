using System;
using Sandbox;

namespace Conna.Inventory;

/// <summary>
/// Methods on an <see cref="InventoryItem"/> or a <see cref="BaseInventory"/> with this attribute will be invoked on the host.
/// </summary>
[CodeGenerator( CodeGeneratorFlags.Instance | CodeGeneratorFlags.WrapMethod, "Conna.Inventory.InventoryItem.InvokeOnHost" )]
[AttributeUsage( AttributeTargets.Method )]
public class HostAttribute : Attribute
{

}
