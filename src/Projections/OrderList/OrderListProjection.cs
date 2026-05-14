using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;

namespace EventSourcingCqrs.Projections.OrderList;

// Pattern from Chapter 13: a list view of placed orders. One row per order,
// inserted when the order is placed and updated when it ships or is cancelled.
//
// The cart-phase events (OrderDrafted, OrderLineAdded, OrderLineRemoved,
// ShippingAddressSet) precede OrderPlaced and are not list-relevant, so the
// projection has no handler for them; the dispatcher and the replayer skip
// events with no registered handler. OrderPlaced carries the final Total in
// its payload, so the projection never accumulates line totals and never
// races a not-yet-inserted row.
//
// Each handler opens a unit of work, makes its one read-model change, and
// commits. CommitAsync advances the checkpoint to the event's global position
// in the same transaction as the write.
public sealed class OrderListProjection
    : IProjection,
      IEventHandler<OrderPlaced>,
      IEventHandler<OrderShipped>,
      IEventHandler<OrderCancelled>
{
    public string Name => "order-list";

    private readonly IOrderListStore _store;

    public OrderListProjection(IOrderListStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task HandleAsync(EventContext<OrderPlaced> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var row = new OrderListRow(
            OrderId: context.Event.OrderId,
            CustomerId: context.Event.CustomerId,
            Status: OrderStatus.Placed,
            Total: context.Event.Total,
            // Business time from the event payload, system time from the metadata.
            PlacedUtc: context.Event.PlacedUtc,
            LastUpdatedUtc: context.Metadata.OccurredUtc);

        await using var uow = await _store.BeginAsync(ct);
        await uow.InsertAsync(row, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<OrderShipped> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        await uow.UpdateStatusAsync(
            context.Event.OrderId, OrderStatus.Shipped, context.Metadata.OccurredUtc, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<OrderCancelled> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        // An order cancelled while still a draft was never placed: no row
        // exists and the update touches nothing, which is the right outcome.
        await using var uow = await _store.BeginAsync(ct);
        await uow.UpdateStatusAsync(
            context.Event.OrderId, OrderStatus.Cancelled, context.Metadata.OccurredUtc, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }
}
