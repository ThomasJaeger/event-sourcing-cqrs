namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// Per-line reservation state, keyed by LineId in the PM. InventoryId is null on a
// failed line; FailureReason is null on a reserved or released line. Sku and
// Quantity are per-line forensic detail from Chapter 10's compensation story; the
// compensating ReleaseInventory needs only InventoryId and LineId, both here.
public sealed record ReservationLineState(
    string Sku,
    int Quantity,
    Guid? InventoryId,
    ReservationLineStatus Status,
    string? FailureReason);
