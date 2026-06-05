using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Fulfillment.ReadModels;

// The SKU-to-InventoryId lookup's persistence port. Lives in
// Domain.Fulfillment.ReadModels because Inventory belongs to Fulfillment and
// read-side ports are bounded-context-owned (ADR 0008), even though the contract
// uses primitives rather than Fulfillment types. SkuToInventoryIdProjection
// writes it from InventoryCreated; the OrderFulfillment process manager reads it
// (commit 21) before each ReserveInventory dispatch to resolve a line's SKU to
// the Inventory aggregate's Guid id.
public interface ISkuToInventoryIdStore
{
    // Opens a unit of work. The projection handler writes its mapping on the
    // unit of work and commits, advancing the checkpoint in the same transaction.
    Task<ISkuToInventoryIdUnitOfWork> BeginAsync(CancellationToken ct);

    // Read path: the process manager looks up one SKU at a time, outside any unit
    // of work, scoped to the current tenant the read resolves from the accessor the
    // same way the write does. Returns null when no inventory has been created for the
    // SKU under that tenant, which the PM treats as a workflow failure routing to
    // compensation.
    Task<Guid?> GetInventoryIdAsync(string sku, CancellationToken ct);

    // Rebuild support: drop every row so a replay starts from empty.
    Task TruncateAsync(CancellationToken ct);
}

// A single projection write: the SKU mapping plus the checkpoint advance, in one
// transaction. Mirrors IOrderListUnitOfWork; CommitAsync takes the projection
// name and position so the store stays projection-agnostic.
public interface ISkuToInventoryIdUnitOfWork : IAsyncDisposable
{
    // Reads the projection's checkpoint inside this unit of work's transaction,
    // so the handler's idempotency check shares isolation with its write.
    Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct);

    // Records a SKU's InventoryId. A SKU maps to one InventoryId for its
    // lifetime, so a redelivered InventoryCreated for the same SKU is a no-op.
    Task RecordAsync(string sku, Guid inventoryId, CancellationToken ct);

    // Stages a notification to publish atomically inside CommitAsync, on the
    // same transaction as the write. No v1 consumer subscribes to this lookup,
    // so its handler stages nothing today; the member keeps the unit-of-work
    // contract uniform across the six read models. A unit of work stages at most
    // one notification, and a second call throws.
    void PublishOnCommit(NotificationEnvelope envelope);

    // Advances the checkpoint to `position` and commits, both in the transaction
    // the mapping write above ran in.
    Task CommitAsync(string projectionName, long position, CancellationToken ct);
}
