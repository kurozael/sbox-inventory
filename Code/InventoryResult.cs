namespace Conna.Inventory;

public enum InventoryResult
{
	Success,
	ItemWasNull,
	ItemAlreadyInInventory,
	ItemNotInInventory,
	DestinationWasNull,
	InsertNotAllowed,
	RemoveNotAllowed,
	TransferNotAllowed,
	ReceiveNotAllowed,
	PlacementNotAllowed,
	StackingNotAllowed,
	InvalidStackCount,
	NoSpaceAvailable,
	SlotSizeMismatch,
	PlacementOutOfBounds,
	PlacementCollision,
	AmountMustBePositive,
	AmountExceedsStack,
	ItemNotStackable,
	CannotCombineWithSelf,
	BothItemsMustBeInInventory,
	DestinationStackFull,
	NoAuthority,
	RequestTimeout
}
