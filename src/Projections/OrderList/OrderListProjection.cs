using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using Microsoft.Extensions.Logging;

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
// Each handler opens a unit of work, reads the projection's checkpoint inside
// the same transaction, and returns early if the event's global position is at
// or below the checkpoint. This handler-level idempotency closes the window
// where startup catch-up and live dispatch overlap, and protects against
// out-of-order redelivery: a stale OrderShipped reaching the handler after
// OrderCancelled has been processed would otherwise clobber the cancel back to
// shipped. Past the skip check, the handler makes its one read-model change
// and commits. CommitAsync advances the checkpoint to the event's global
// position in the same transaction as the write.
public sealed class OrderListProjection
    : IProjection,
      IEventHandler<OrderPlaced>,
      IEventHandler<OrderShipped>,
      IEventHandler<OrderCancelled>,
      IEventHandler<OrderCompleted>,
      IEventHandler<ShipmentScheduled>,
      IEventHandler<ShipmentReturned>
{
    public string Name => "order-list";

    private readonly IOrderListStore _store;
    private readonly ILogger<OrderListProjection> _logger;

    public OrderListProjection(IOrderListStore store, ILogger<OrderListProjection> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    public async Task HandleAsync(EventContext<OrderPlaced> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            // At-least-once redelivery of an already-processed event. The uow
            // disposes without commit, rolling the empty transaction back.
            return;
        }
        var row = new OrderListRow(
            OrderId: context.Event.OrderId,
            CustomerId: context.Event.CustomerId,
            Status: OrderStatus.Placed,
            Total: context.Event.Total,
            // Business time from the event payload, system time from the metadata.
            PlacedUtc: context.Event.PlacedUtc,
            LastUpdatedUtc: context.Metadata.OccurredUtc,
            // A freshly placed order is never returned; the ShipmentReturned
            // handler sets the return state later.
            IsReturned: false,
            ReturnedUtc: null);
        await uow.InsertAsync(row, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<OrderShipped> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        await uow.UpdateStatusAsync(
            context.Event.OrderId, OrderStatus.Shipped, context.Metadata.OccurredUtc, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<OrderCancelled> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        // An order cancelled while still a draft was never placed: no row
        // exists and the update touches nothing, which is the right outcome.
        await uow.UpdateStatusAsync(
            context.Event.OrderId, OrderStatus.Cancelled, context.Metadata.OccurredUtc, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // OrderCompleted: the order reached its lifecycle terminal. Last-observed
    // label like Shipped and Cancelled; Order.Complete guards the Placed-to-
    // Completed transition (Decision 6), so the projection trusts the event.
    public async Task HandleAsync(EventContext<OrderCompleted> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        await uow.UpdateStatusAsync(
            context.Event.OrderId, OrderStatus.Completed, context.Metadata.OccurredUtc, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // ShipmentScheduled records the ShipmentId -> OrderId mapping so a later
    // ShipmentReturned can resolve its order. ShipmentScheduled is the only
    // shipment event carrying both ids (ADR 0020); it makes no order_list change.
    public async Task HandleAsync(EventContext<ShipmentScheduled> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        await uow.InsertShipmentMappingAsync(
            context.Event.ShipmentId, context.Event.OrderId, context.Event.ScheduledUtc, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // ShipmentReturned carries only ShipmentId (ADR 0020). Resolve the OrderId
    // through the mapping recorded on ShipmentScheduled, then mark the order
    // returned. An unmapped shipment (scheduled before this projection observed
    // it) no-ops with a debug log; the checkpoint still advances so the event is
    // not reprocessed.
    public async Task HandleAsync(EventContext<ShipmentReturned> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var orderId = await uow.GetOrderIdByShipmentIdAsync(context.Event.ShipmentId, ct);
        if (orderId is null)
        {
            _logger.LogDebug(
                "ShipmentReturned for shipment {ShipmentId} has no order_list_shipments " +
                "mapping; skipping return-state update.",
                context.Event.ShipmentId);
        }
        else
        {
            await uow.MarkReturnedAsync(
                orderId.Value, context.Event.ReturnedUtc, context.Metadata.OccurredUtc, ct);
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }
}
