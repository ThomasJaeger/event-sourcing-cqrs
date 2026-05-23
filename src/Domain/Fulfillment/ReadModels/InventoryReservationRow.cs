namespace EventSourcingCqrs.Domain.Fulfillment.ReadModels;

// One row of InventoryDashboardProjection's projection-private per-reservation
// lookup (read_models.inventory_reservations, D10). Recorded on InventoryReserved
// and read on InventoryReleased to recover the quantity the release event does
// not carry (the lean-compensating-event shape, ADR 0020). Not a public read
// model: no store port exposes it directly and no query handler reads it.
public sealed record InventoryReservationRow(
    Guid InventoryId,
    Guid OrderId,
    Guid LineId,
    int Quantity,
    DateTime ReservedUtc);
