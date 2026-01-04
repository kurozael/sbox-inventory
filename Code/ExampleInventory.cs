using System;
using System.Collections.Generic;

namespace Sandbox.Inventory;

/// <summary>
/// An example inventory class.
/// </summary>
public class ExampleInventory( Guid id, int width, int height ) : BaseInventory( id, width, height );
