namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// The order-list read model's persistence port. Lives in Domain.Sales.ReadModels
// so the row's typed OrderStatus and Money stay reachable without forcing
// Domain.Abstractions to depend on Domain. See ADR 0008.
public interface IOrderListStore
{
    // Opens a unit of work. The handler writes its one row change on the unit
    // of work and then commits, which advances the checkpoint in the same
    // transaction as the write.
    Task<IOrderListUnitOfWork> BeginAsync(CancellationToken ct);

    // Read path: used by the rebuild test, and by query handlers later.
    Task<OrderListRow?> GetAsync(Guid orderId, CancellationToken ct);

    // Paged read path: backs Application's ListOrders query handler. Rows
    // return newest-first by PlacedUtc. The implementation clamps oversized
    // limits internally so a misbehaving caller can't pull every row in one
    // request; Phase 7's API binding adds the user-visible validation.
    Task<IReadOnlyList<OrderListRow>> GetPageAsync(int offset, int limit, CancellationToken ct);

    // Rebuild support: drop every row so a replay starts from empty.
    Task TruncateAsync(CancellationToken ct);
}

// A single projection write: the row change plus the checkpoint advance, in one
// transaction. CommitAsync takes the projection name and position rather than
// baking the name into the store. The store is shared infrastructure; the
// projection knows its own identity and passes it.
public interface IOrderListUnitOfWork : IAsyncDisposable
{
    // Reads the projection's checkpoint inside this unit of work's transaction.
    // The projection calls this right after BeginAsync to skip an at-least-once
    // redelivery whose global position is at or below what it has already
    // processed. Disposing the uow without a CommitAsync rolls the empty
    // transaction back, so the skip path costs one SELECT.
    Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct);

    Task InsertAsync(OrderListRow row, CancellationToken ct);

    Task UpdateStatusAsync(
        Guid orderId,
        OrderStatus status,
        DateTime lastUpdatedUtc,
        CancellationToken ct);

    // Records the ShipmentId -> OrderId mapping from ShipmentScheduled so a later
    // ShipmentReturned (which carries only ShipmentId) can resolve its OrderId.
    // ON CONFLICT DO NOTHING: a redelivered ShipmentScheduled keeps the first row.
    Task InsertShipmentMappingAsync(
        Guid shipmentId,
        Guid orderId,
        DateTime scheduledUtc,
        CancellationToken ct);

    // Resolves the OrderId for a returned shipment, or null when no mapping
    // exists (a shipment scheduled before this projection observed it). The
    // ShipmentReturned handler no-ops on null. See ADR 0020.
    Task<Guid?> GetOrderIdByShipmentIdAsync(Guid shipmentId, CancellationToken ct);

    // Marks the order returned: sets is_returned and returned_utc. Touches zero
    // rows if no order_list row exists, which is harmless.
    Task MarkReturnedAsync(
        Guid orderId,
        DateTime returnedUtc,
        DateTime lastUpdatedUtc,
        CancellationToken ct);

    // Advances the projection's checkpoint to `position` and commits, both in
    // the transaction the row write above ran in.
    Task CommitAsync(string projectionName, long position, CancellationToken ct);
}
