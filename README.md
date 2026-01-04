# Tetris Inventory

A flexible, networked, grid-based inventory system for s&box featuring Tetris-style item placement, stacking, and automatic synchronization across clients.

## Features

- **Grid-based storage** with configurable dimensions
- **Variable item sizes** (Tetris-style packing)
- **Item stacking** with customizable stack limits
- **Built-in networking** with host authority model
- **Drag-and-drop UI** support with swap and split operations
- **Auto-sort and consolidation** utilities

---

## Core Components

| Class | Description |
|-------|-------------|
| `BaseInventory` | Abstract base class for grid inventories |
| `InventoryItem` | Abstract base class for items |
| `InventorySlot` | Readonly struct representing item position (X, Y, Width, Height) |
| `InventoryResult` | Enum of operation outcomes |
| `InventorySystem` | GameObjectSystem that tracks all registered inventories |
| `NetworkedInventory` | Handles client→host RPC routing for inventory operations |

---

## Getting Started

### 1. Create an Inventory Class

Inherit from `BaseInventory` and specify dimensions:

```csharp
public sealed class PlayerInventory : BaseInventory
{
    public PlayerInventory( Guid id ) : base( id, 10, 6 ) // 10 columns × 6 rows
    {
    }
}
```

### 2. Create Item Classes

Inherit from `InventoryItem` and override properties as needed:

```csharp
public class AmmoItem : InventoryItem
{
    public override string DisplayName => "Ammo";
    public override string Category => "Consumables";
    public override int MaxStackSize => 64;
}

public class LongItem : InventoryItem
{
    public override string DisplayName => "Rifle";
    public override int Width => 4;
    public override int Height => 1;
    public override int MaxStackSize => 1;
}

public class LargeItem : InventoryItem
{
    public override string DisplayName => "Armor";
    public override int Width => 2;
    public override int Height => 3;
}
```

### 3. Initialize the Inventory

```csharp
public class Player : Component
{
    public PlayerInventory Inventory { get; private set; }

    protected override void OnAwake()
    {
        // Create inventory with a unique ID (Component.Id works well)
        Inventory = new PlayerInventory( Id );
        
        // Enable networking for multiplayer sync
        Inventory.EnableNetworking();
    }
}
```

---

## Basic Operations

All operations return an `InventoryResult` indicating success or the specific failure reason.

### Adding Items

```csharp
// Add to first available position (merges into existing stacks if possible)
var result = inventory.TryAdd( new AmmoItem() );

// Add at specific position (no stack merging)
result = inventory.TryAddAt( item, x: 2, y: 0 );
```

### Removing Items

```csharp
var result = inventory.TryRemove( item );
```

### Moving Items

```csharp
// Move to specific position
var result = inventory.TryMove( item, newX: 3, newY: 2 );

// Move or swap with item at target position
result = inventory.TryMoveOrSwap( item, x: 3, y: 2, out var swappedItem );
```

### Querying the Inventory

```csharp
// Check if item exists
bool exists = inventory.Contains( item );

// Get item at specific cell
InventoryItem itemAtPos = inventory.GetItemAt( x: 2, y: 3 );

// Get all items overlapping a rectangle
List<InventoryItem> items = inventory.GetItemsInRect( x: 0, y: 0, w: 3, h: 3 );

// Get an item's position
if ( inventory.TryGetSlot( item, out var slot ) )
{
    Log.Info( $"Item at ({slot.X}, {slot.Y}) size {slot.W}x{slot.H}" );
}

// Iterate all entries
foreach ( var entry in inventory.Entries )
{
    Log.Info( $"{entry.Item.DisplayName} at ({entry.Slot.X}, {entry.Slot.Y})" );
}
```

### Checking Placement

```csharp
// Check if item can be placed at position (excluding self from collision)
bool canPlace = inventory.CanPlaceItemAt( item, x: 5, y: 2, excludeFromCollision: item );

// Check if move or swap is possible
bool canMoveOrSwap = inventory.CanMoveOrSwap( item, x: 5, y: 2 );
```

---

## Stack Operations

### Setting Stack Count

```csharp
var ammo = new AmmoItem();
ammo.SetStackCount( 32 ); // Must be between 1 and MaxStackSize
inventory.TryAdd( ammo );
```

### Splitting Stacks

```csharp
// Take amount from a stack and place at new position
var result = inventory.TrySplitAndPlace( 
    item: ammoStack, 
    splitAmount: 16, 
    slot: new InventorySlot( x: 5, y: 0, w: 1, h: 1 ), 
    out var newStack 
);

// Take amount and transfer to another inventory
result = inventory.TrySplitAndTransferTo( 
    item: ammoStack, 
    splitAmount: 16, 
    destination: otherInventory, 
    out var transferred 
);
```

### Combining Stacks

```csharp
// Combine within same inventory
var result = inventory.TryCombineStacks( 
    source: smallStack, 
    destination: largerStack, 
    amount: -1,  // -1 = move as much as possible
    out int moved 
);

// Combine across inventories
result = inventory.TryCombineStacksTo( 
    source: myStack, 
    destination: theirStack, 
    otherInventory, 
    amount: 10, 
    out moved 
);
```

### Consolidating Stacks

Automatically merge all partial stacks of the same item type:

```csharp
var result = inventory.TryConsolidateStacks();
```

---

## Transfer Operations

### Simple Transfer

```csharp
// Transfer to first available position
var result = inventory.TryTransferTo( item, destination );

// Transfer to specific position
result = inventory.TryTransferToAt( item, destination, x: 0, y: 0 );

// Transfer or swap with item at target
result = inventory.TryTransferOrSwapAt( item, destination, x: 0, y: 0, out var swapped );
```

### Swapping Between Inventories

```csharp
var result = inventory.TrySwapBetween( myItem, theirItem, otherInventory );
```

### Partial Transfer

```csharp
// Take specific amount and transfer
var result = inventory.TryTakeAndTransferTo( 
    item: ammoStack, 
    amount: 20, 
    destination: otherInventory, 
    out var transferred 
);
```

---

## Utility Operations

### Auto-Sort

Reorganizes items to pack efficiently (largest items first, top-left):

```csharp
var result = inventory.AutoSort();
```

### Clear All

```csharp
var result = inventory.ClearAll();
```

---

## Networking

The inventory system uses a host-authoritative model. Clients send requests to the host, which validates and executes operations, then broadcasts changes to all subscribers.

### Enabling Networking

```csharp
inventory.EnableNetworking();
```

### Subscribing Clients

Clients must be subscribed to receive updates:

```csharp
// On host, add subscriber when player needs access
inventory.AddSubscriber( connection.Id );

// Remove when no longer needed
inventory.RemoveSubscriber( connection.Id );
```

### Network Operations

For networked inventories, use the `Network` accessor for async operations:

```csharp
// These return Task<InventoryResult> and handle client→host routing automatically
await inventory.Network.TryMove( item, newX, newY );
await inventory.Network.TryMoveOrSwap( item, x, y );
await inventory.Network.TrySwap( itemA, itemB );
await inventory.Network.TryTransferToAt( item, destination, x, y );
await inventory.Network.TryCombineStacks( source, dest, amount );
await inventory.Network.TryTakeAndPlace( item, amount, slot );
await inventory.Network.AutoSort();
await inventory.Network.ConsolidateStacks();
```

### Authority Model

```csharp
// Check if this client can modify the inventory directly
if ( inventory.HasAuthority )
{
    // Direct operations allowed (host or non-networked)
    inventory.TryAdd( item );
}
else
{
    // Must use Network accessor
    await inventory.Network.TryMoveOrSwap( item, x, y );
}
```

---

## Events

```csharp
inventory.OnItemAdded += ( entry ) => 
    Log.Info( $"Added {entry.Item.DisplayName} at ({entry.Slot.X}, {entry.Slot.Y})" );

inventory.OnItemRemoved += ( entry ) => 
    Log.Info( $"Removed {entry.Item.DisplayName}" );

inventory.OnItemMoved += ( item, newX, newY ) => 
    Log.Info( $"Moved {item.DisplayName} to ({newX}, {newY})" );

inventory.OnInventoryChanged += () => 
    Log.Info( "Inventory contents changed" );
```

---

## Custom Item Data

Override serialization methods to sync custom item properties:

```csharp
public class WeaponItem : InventoryItem
{
    public int Durability { get; set; } = 100;
    public string Enchantment { get; set; }

    public override Dictionary<string, object> SerializeMetadata()
    {
        var data = base.SerializeMetadata();
        data["Durability"] = Durability;
        data["Enchantment"] = Enchantment;
        return data;
    }

    public override void DeserializeNetworkData( Dictionary<string, object> data )
    {
        base.DeserializeNetworkData( data );
        
        if ( data.TryGetValue( "Durability", out var dur ) )
            Durability = (int)dur;
        if ( data.TryGetValue( "Enchantment", out var ench ) )
            Enchantment = (string)ench;
    }

    // Mark dirty when properties change to trigger network sync
    public void TakeDamage( int amount )
    {
        Durability -= amount;
        MarkDirty();
    }
}
```

---

## Custom Stacking Rules

Override `CanStackWith` for items that need metadata comparison:

```csharp
public class ColoredGemItem : InventoryItem
{
    public Color GemColor { get; set; }
    public override int MaxStackSize => 16;

    public override bool CanStackWith( InventoryItem other )
    {
        if ( !base.CanStackWith( other ) )
            return false;

        // Only stack gems of the same color
        return other is ColoredGemItem gem && gem.GemColor == GemColor;
    }

    public override InventoryItem CreateStackClone( int stackCount )
    {
        var clone = (ColoredGemItem)base.CreateStackClone( stackCount );
        clone.GemColor = GemColor;
        return clone;
    }
}
```

---

## Inventory Restrictions

Override validation methods to implement custom rules:

```csharp
public class WeaponOnlyInventory : BaseInventory
{
    public WeaponOnlyInventory( Guid id ) : base( id, 5, 2 ) { }

    // Only accept weapons
    protected override bool CanInsertItem( InventoryItem item )
    {
        return item is WeaponItem;
    }

    // Prevent removing equipped weapon
    protected override bool CanRemoveItem( InventoryItem item )
    {
        if ( item is WeaponItem weapon && weapon.IsEquipped )
            return false;
        return true;
    }

    // Custom placement rules (e.g., reserved slots)
    protected override bool CanPlaceAt( InventoryItem item, int x, int y, int w, int h )
    {
        // First row is for primary weapons only
        if ( y == 0 && item is not PrimaryWeaponItem )
            return false;
        return true;
    }
}
```

---

## UI Integration Example

### Razor Component (InventoryPanel.razor)

```html
@using Sandbox.UI
@inherits Panel

<root class="inventory-panel">
    <div class="inventory-grid" style="width: @GridWidth; height: @GridHeight;">
        @* Render grid cells *@
        @for ( int row = 0; row < Inventory.Height; row++ )
        {
            @for ( int col = 0; col < Inventory.Width; col++ )
            {
                <div class="grid-cell" style="left: @(col * CellSize)px; top: @(row * CellSize)px;"></div>
            }
        }

        @* Render items *@
        @foreach ( var entry in Inventory.Entries )
        {
            <div class="inventory-item"
                 style="left: @(entry.Slot.X * CellSize)px; 
                        top: @(entry.Slot.Y * CellSize)px;
                        width: @(entry.Slot.W * CellSize)px; 
                        height: @(entry.Slot.H * CellSize)px;"
                 onmousedown="@(e => OnItemMouseDown(e, entry.Item))">
                 
                <label>@entry.Item.DisplayName</label>
                
                @if ( entry.Item.MaxStackSize > 1 )
                {
                    <label class="stack-count">@entry.Item.StackCount</label>
                }
            </div>
        }
    </div>
</root>

@code {
    public BaseInventory Inventory { get; set; }
    private const float CellSize = 48f;

    protected override void OnAfterTreeRender( bool firstTime )
    {
        if ( firstTime && Inventory != null )
        {
            Inventory.OnInventoryChanged += StateHasChanged;
        }
    }

    private void OnItemMouseDown( PanelEvent e, InventoryItem item )
    {
        // Begin drag operation
    }

    // Drag-and-drop implementation...
    private async void OnDrop( int x, int y, InventoryItem item )
    {
        await Inventory.Network.TryMoveOrSwap( item, x, y );
    }
}
```

### Handling Drag-and-Drop

```csharp
// Check if drop is valid during drag
private void UpdateDragPreview( int targetX, int targetY )
{
    CanDrop = Inventory.CanMoveOrSwap( DraggedItem, targetX, targetY );
}

// Execute drop
private async void OnDrop()
{
    if ( IsSplitting )
    {
        // Shift+drag to split stack
        var splitAmount = DraggedItem.StackCount / 2;
        var slot = new InventorySlot( TargetX, TargetY, DraggedItem.Width, DraggedItem.Height );
        await Inventory.Network.TryTakeAndPlace( DraggedItem, splitAmount, slot );
    }
    else
    {
        await Inventory.Network.TryMoveOrSwap( DraggedItem, TargetX, TargetY );
    }
}
```

---

## InventoryResult Values

| Result | Description |
|--------|-------------|
| `Success` | Operation completed successfully |
| `ItemWasNull` | Item parameter was null |
| `ItemAlreadyInInventory` | Item already exists in this inventory |
| `ItemNotInInventory` | Item not found in this inventory |
| `DestinationWasNull` | Destination inventory was null |
| `InsertNotAllowed` | `CanInsertItem` returned false |
| `RemoveNotAllowed` | `CanRemoveItem` returned false |
| `TransferNotAllowed` | Transfer validation failed |
| `ReceiveNotAllowed` | Destination refused the transfer |
| `PlacementNotAllowed` | `CanPlaceAt` returned false |
| `StackingNotAllowed` | Items cannot stack together |
| `InvalidStackCount` | Stack count out of valid range |
| `NoSpaceAvailable` | No room for item |
| `SlotSizeMismatch` | Slot dimensions don't match item |
| `PlacementOutOfBounds` | Position outside grid |
| `PlacementCollision` | Another item occupies the space |
| `AmountMustBePositive` | Amount must be > 0 |
| `AmountExceedsStack` | Amount larger than stack count |
| `ItemNotStackable` | Item MaxStackSize is 1 |
| `CannotCombineWithSelf` | Cannot combine item with itself |
| `BothItemsMustBeInInventory` | Both items must be present |
| `DestinationStackFull` | Target stack has no space |
| `NoAuthority` | Client lacks authority (use Network accessor) |
| `RequestTimeout` | Network request timed out |

---

## Best Practices

1. **Always check results**: Handle `InventoryResult` to provide feedback or handle failures.

2. **Use Network accessor for clients**: In multiplayer, always use `inventory.Network.*` methods from clients.

3. **Subscribe players appropriately**: Only subscribe connections that need real-time updates.

4. **Call MarkDirty()**: When modifying custom item properties, call `MarkDirty()` to trigger network sync.

5. **Dispose inventories**: Call `Dispose()` when the inventory owner is destroyed to unregister from the system.

```csharp
public override void OnDestroy()
{
    Inventory?.Dispose();
}
```

---

## Complete Example: Loot Container

```csharp
public class LootContainer : Component, IInteractable
{
    public BaseInventory Inventory { get; private set; }

    protected override void OnAwake()
    {
        Inventory = new ContainerInventory( Id );
        Inventory.EnableNetworking();
        
        // Spawn random loot
        if ( Networking.IsHost )
        {
            SpawnRandomLoot();
        }
    }

    private void SpawnRandomLoot()
    {
        for ( int i = 0; i < Random.Shared.Next( 3, 8 ); i++ )
        {
            var item = CreateRandomItem();
            Inventory.TryAdd( item );
        }
    }

    public void OnInteract( Player player )
    {
        // Subscribe player to receive inventory updates
        Inventory.AddSubscriber( player.ConnectionId );
        
        // Open UI (handled elsewhere)
        player.OpenContainer( this );
    }

    public void OnStopInteract( Player player )
    {
        Inventory.RemoveSubscriber( player.ConnectionId );
    }

    protected override void OnDestroy()
    {
        Inventory?.Dispose();
    }
}

public class ContainerInventory : BaseInventory
{
    public ContainerInventory( Guid id ) : base( id, 6, 4 ) { }
}
```
