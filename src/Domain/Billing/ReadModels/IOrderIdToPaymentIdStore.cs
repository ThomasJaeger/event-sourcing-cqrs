namespace EventSourcingCqrs.Domain.Billing.ReadModels;

// The OrderId-to-PaymentId lookup's persistence port. Lives in
// Domain.Billing.ReadModels because Payment belongs to Billing and read-side
// ports are bounded-context-owned (ADR 0008), even though the contract uses
// primitives rather than Billing types. OrderIdToPaymentIdProjection writes it
// from PaymentAuthorized; the ReturnProcessManager reads it (commit 26b) before
// dispatching VoidPayment, to resolve a returned order to its payment without
// reading another process manager's internal state. Cross-PM data acquisition
// goes through a read model, the same way cross-context lookups do.
public interface IOrderIdToPaymentIdStore
{
    // Opens a unit of work. The projection handler writes its mapping on the unit
    // of work and commits, advancing the checkpoint in the same transaction.
    Task<IOrderIdToPaymentIdUnitOfWork> BeginAsync(CancellationToken ct);

    // Read path: the process manager looks up one order at a time, outside any
    // unit of work. Returns null when no payment has been authorized for the
    // order, which the PM treats as a workflow failure routing to Stuck.
    Task<Guid?> GetPaymentIdAsync(Guid orderId, CancellationToken ct);

    // Rebuild support: drop every row so a replay starts from empty.
    Task TruncateAsync(CancellationToken ct);
}

// A single projection write: the OrderId-to-PaymentId mapping plus the checkpoint
// advance, in one transaction. Mirrors ISkuToInventoryIdUnitOfWork.
public interface IOrderIdToPaymentIdUnitOfWork : IAsyncDisposable
{
    // Reads the projection's checkpoint inside this unit of work's transaction, so
    // the handler's idempotency check shares isolation with its write.
    Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct);

    // Records an order's PaymentId. An order has one authorized payment, so a
    // redelivered PaymentAuthorized for the same order is a no-op.
    Task RecordAsync(Guid orderId, Guid paymentId, CancellationToken ct);

    // Advances the checkpoint to `position` and commits, both in the transaction
    // the mapping write above ran in.
    Task CommitAsync(string projectionName, long position, CancellationToken ct);
}
