using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales;

// The memento of an Order's rehydrated state: every field Order.Apply mutates, and nothing else. A
// snapshot carries no version of its own in the record; the aggregate version it was taken at rides
// alongside it through RestoreFrom, so a restore replays only the tail. Pattern from Chapter 12:
// Snapshots. SnapshotSchemaVersion is the record's own shape version: if the snapshot shape ever
// changes, an old row is discarded and the aggregate rebuilt from events rather than upcast, so this
// is the field an S2 row carries to know whether its shape still matches.
public sealed record OrderSnapshot(
    Guid OrderId,
    Guid CustomerId,
    OrderStatus Status,
    IReadOnlyList<OrderLine> Lines,
    Address? ShippingAddress)
{
    public const int SnapshotSchemaVersion = 1;
}
