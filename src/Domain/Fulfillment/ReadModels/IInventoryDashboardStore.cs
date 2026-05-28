using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Fulfillment.ReadModels;

// The inventory-dashboard read model's persistence port. Lives in
// Domain.Fulfillment.ReadModels because InventoryDashboard derives from the
// Fulfillment inventory events. See ADR 0008.
public interface IInventoryDashboardStore
{
    Task<IInventoryDashboardUnitOfWork> BeginAsync(CancellationToken ct);

    // Read paths. GetBySkuAsync uses the sku UNIQUE index for a single-SKU
    // lookup; GetAllAsync backs the dashboard list query (commit 13).
    Task<InventoryDashboardRow?> GetBySkuAsync(string sku, CancellationToken ct);
    Task<IReadOnlyList<InventoryDashboardRow>> GetAllAsync(CancellationToken ct);

    // Rebuild support: drop every row from both tables so a replay starts empty.
    Task TruncateAsync(CancellationToken ct);
}

// One projection write: the dashboard mutation, the per-reservation lookup
// change, and the checkpoint advance, all on one transaction. CommitAsync makes
// them durable together; DisposeAsync without a commit rolls them back.
//
// Reserve and release are symmetric (a pure +/- on reserved_quantity), so they
// share AdjustReservedAsync with a signed delta rather than splitting into two
// named methods. On-hand adjustment is the same signed-delta shape.
public interface IInventoryDashboardUnitOfWork : IAsyncDisposable
{
    Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct);

    // InventoryCreated: create the dashboard row with zero on-hand and reserved.
    // ON CONFLICT DO NOTHING so a redelivered InventoryCreated keeps the first row.
    Task CreateDashboardAsync(Guid inventoryId, string sku, DateTime lastUpdatedUtc, CancellationToken ct);

    // InventoryAdjusted: add the signed delta to on_hand_quantity. Returns the
    // mutated row's sku via RETURNING, or null when no row matched (the ADR 0020
    // orphan case), so the handler routes the notification by sku without a
    // second read.
    Task<string?> AdjustOnHandAsync(Guid inventoryId, int quantityDelta, DateTime lastUpdatedUtc, CancellationToken ct);

    // InventoryReserved (positive delta) and InventoryReleased (negative delta,
    // the recovered reservation quantity): add the signed delta to reserved_quantity.
    // Returns the mutated row's sku via RETURNING, or null when no row matched.
    Task<string?> AdjustReservedAsync(Guid inventoryId, int reservedDelta, DateTime lastUpdatedUtc, CancellationToken ct);

    // The projection-private per-reservation lookup (D10). Written on reserve,
    // read by the full key on release to recover the quantity, then deleted.
    Task InsertReservationAsync(InventoryReservationRow row, CancellationToken ct);
    Task<InventoryReservationRow?> GetReservationAsync(
        Guid inventoryId, Guid orderId, Guid lineId, CancellationToken ct);
    Task DeleteReservationAsync(Guid inventoryId, Guid orderId, Guid lineId, CancellationToken ct);

    // Stages a notification to publish atomically inside CommitAsync, on the
    // same transaction as the writes, so a committed change always notifies and a
    // rolled-back one never does. The handler stages only when a UI-relevant row
    // changed; a unit of work stages at most one notification, and a second call
    // throws.
    void PublishOnCommit(NotificationEnvelope envelope);

    Task CommitAsync(string projectionName, long position, CancellationToken ct);
}
