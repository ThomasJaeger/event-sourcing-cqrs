using EventSourcingCqrs.Domain.Sales;

namespace EventSourcingCqrs.Projections.OrderList;

// The order-list read model's persistence port. Lives next to the projection,
// not in Domain.Abstractions: it is the projection's own seam for unit testing
// against an in-memory store, not a cross-cutting abstraction.
public interface IOrderListStore
{
    // Opens a unit of work. The handler writes its one row change on the unit
    // of work and then commits, which advances the checkpoint in the same
    // transaction as the write.
    Task<IOrderListUnitOfWork> BeginAsync(CancellationToken ct);

    // Read path: used by the rebuild test, and by query handlers later.
    Task<OrderListRow?> GetAsync(Guid orderId, CancellationToken ct);

    // Rebuild support: drop every row so a replay starts from empty.
    Task TruncateAsync(CancellationToken ct);
}

// A single projection write: the row change plus the checkpoint advance, in one
// transaction. CommitAsync takes the projection name and position rather than
// baking the name into the store. The store is shared infrastructure; the
// projection knows its own identity and passes it.
public interface IOrderListUnitOfWork : IAsyncDisposable
{
    Task InsertAsync(OrderListRow row, CancellationToken ct);

    Task UpdateStatusAsync(
        Guid orderId,
        OrderStatus status,
        DateTime lastUpdatedUtc,
        CancellationToken ct);

    // Advances the projection's checkpoint to `position` and commits, both in
    // the transaction the row write above ran in.
    Task CommitAsync(string projectionName, long position, CancellationToken ct);
}
