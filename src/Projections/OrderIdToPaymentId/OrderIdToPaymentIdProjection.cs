using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Billing.ReadModels;

namespace EventSourcingCqrs.Projections.OrderIdToPaymentId;

// An OrderId-to-PaymentId lookup over the Billing context's PaymentAuthorized
// events, populated for the ReturnProcessManager to resolve a returned order to
// its payment (commit 26b) without reading the OrderFulfillment process manager's
// state. Cross-PM data acquisition goes through a read model, not a direct
// PM-state read. Structural template is SkuToInventoryIdProjection: open a unit
// of work, read the checkpoint in the same transaction, skip if the event's
// position is at or below it, make the one write, and commit, advancing the
// checkpoint atomically.
public sealed class OrderIdToPaymentIdProjection
    : IProjection,
      IEventHandler<PaymentAuthorized>
{
    public string Name => ProjectionNames.OrderIdToPaymentId;

    private readonly IOrderIdToPaymentIdStore _store;

    public OrderIdToPaymentIdProjection(IOrderIdToPaymentIdStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task HandleAsync(EventContext<PaymentAuthorized> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            // At-least-once redelivery of an already-processed event. The uow
            // disposes without commit, rolling the empty transaction back.
            return;
        }
        await uow.RecordAsync(context.Event.OrderId, context.Event.PaymentId, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }
}
