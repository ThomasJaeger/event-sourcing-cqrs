using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// The customer-summary read model's persistence port. Lives in
// Domain.Sales.ReadModels because CustomerSummary derives from Sales events
// (OrderPlaced, OrderCancelled) and the row carries the domain's Money. See
// ADR 0008.
public interface ICustomerSummaryStore
{
    // Opens a unit of work. A handler applies its summary mutation and its
    // per-order lookup write on the unit of work, then commits, which advances
    // the checkpoint in the same transaction.
    Task<ICustomerSummaryUnitOfWork> BeginAsync(CancellationToken ct);

    // Read path: used by the rebuild test and the query handler.
    Task<CustomerSummaryRow?> GetAsync(Guid customerId, CancellationToken ct);

    // Rebuild support: drop every row from both tables so a replay starts empty.
    Task TruncateAsync(CancellationToken ct);
}

// One projection write: the summary mutation, the per-order lookup change, and
// the checkpoint advance, all on one transaction. CommitAsync makes them
// durable together; DisposeAsync without a commit rolls them back.
//
// Placement and cancellation are distinct mutations, so they are distinct
// methods. ADR 0019's Consequences sketched a single UpsertSummaryAsync; the
// split reads clearer as a study text and the architectural commitment (net
// through projection-private state with a per-order lookup) is unchanged.
public interface ICustomerSummaryUnitOfWork : IAsyncDisposable
{
    // Reads the projection's checkpoint inside this unit of work's transaction,
    // so the idempotency check shares isolation with the writes.
    Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct);

    // OrderPlaced: increment order_count by 1, add total to the lifetime value,
    // set last_order_utc = MAX(last_order_utc, placedUtc). Upsert on customerId.
    Task ApplyPlacementAsync(
        Guid customerId,
        Money total,
        DateTime placedUtc,
        DateTime lastUpdatedUtc,
        CancellationToken ct);

    // OrderCancelled netting: decrement order_count by 1, subtract total from
    // the lifetime value, leave last_order_utc alone (ADR 0019's accepted
    // staleness). The projection calls this only after a found lookup row, so
    // the summary row always exists.
    Task ApplyCancellationAsync(
        Guid customerId,
        Money total,
        DateTime lastUpdatedUtc,
        CancellationToken ct);

    // The projection-private per-order lookup (D8). Written on placement, read
    // by order id on cancellation, deleted once the cancellation nets out.
    Task InsertOrderAsync(CustomerSummaryOrderRow row, CancellationToken ct);

    Task<CustomerSummaryOrderRow?> GetOrderByOrderIdAsync(Guid orderId, CancellationToken ct);

    Task DeleteOrderAsync(Guid customerId, Guid orderId, CancellationToken ct);

    // Stages a notification to publish atomically inside CommitAsync, on the
    // same transaction as the writes, so a committed change always notifies and a
    // rolled-back one never does. The handler stages only when a UI-relevant row
    // changed; a unit of work stages at most one notification, and a second call
    // throws.
    void PublishOnCommit(NotificationEnvelope envelope);

    // Advances the projection's checkpoint to `position` and commits, both in
    // the transaction the writes above ran in.
    Task CommitAsync(string projectionName, long position, CancellationToken ct);
}
