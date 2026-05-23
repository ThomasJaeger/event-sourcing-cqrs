using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;

namespace EventSourcingCqrs.Projections.OrderDetail;

// Pattern from Chapter 13: the order detail read model. Subscribes across three
// contexts and maintains a header row, line items, and a JSONB event timeline with
// one entry per observed event. This commit ships the eight Sales handlers plus the
// ShipmentScheduled mapping populator (nine of the sixteen subscriptions); the three
// shipment-update handlers and the four Billing handlers land in commit 17.
//
// Every handler reads the checkpoint, applies its writes, appends one timeline
// entry, and commits in one transaction; the skip-guard makes redelivery a no-op.
// All nine events here carry OrderId on the payload, so no cross-aggregate lookup is
// needed (that is commit 17, for the shipment-update and payment follow-on events).
public sealed class OrderDetailProjection
    : IProjection,
      IEventHandler<OrderDrafted>,
      IEventHandler<OrderLineAdded>,
      IEventHandler<OrderLineRemoved>,
      IEventHandler<ShippingAddressSet>,
      IEventHandler<OrderPlaced>,
      IEventHandler<OrderShipped>,
      IEventHandler<OrderCancelled>,
      IEventHandler<OrderCompleted>,
      IEventHandler<ShipmentScheduled>
{
    public string Name => "order-detail";

    private readonly IOrderDetailStore _store;
    private readonly JsonSerializerOptions _jsonOptions;

    // The shared JsonSerializerOptions singleton (registered by AddPostgresEventStore)
    // is injected so the timeline payload serialises byte-identically to the event
    // store's own payload column. ILogger arrives in commit 17 with the not-found
    // handlers that use it.
    public OrderDetailProjection(IOrderDetailStore store, JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        _store = store;
        _jsonOptions = jsonOptions;
    }

    public async Task HandleAsync(EventContext<OrderDrafted> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        await uow.CreateHeaderAsync(e.OrderId, e.CustomerId, context.Metadata.OccurredUtc, ct);
        await AppendTimelineAsync(uow, e.OrderId, context, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<OrderLineAdded> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        await uow.InsertLineAsync(
            new OrderDetailLineRow(e.OrderId, e.LineId, e.Sku, e.Quantity, e.UnitPrice), ct);
        await AppendTimelineAsync(uow, e.OrderId, context, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<OrderLineRemoved> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        await uow.DeleteLineAsync(e.OrderId, e.LineId, ct);
        await AppendTimelineAsync(uow, e.OrderId, context, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<ShippingAddressSet> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        await uow.SetShippingAddressAsync(
            e.OrderId, e.ShippingAddress, context.Metadata.OccurredUtc, ct);
        await AppendTimelineAsync(uow, e.OrderId, context, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<OrderPlaced> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        // Business time (e.PlacedUtc) stamps placed_utc; envelope time stamps
        // last_updated. ApplyPlacedAsync also writes the total.
        await uow.ApplyPlacedAsync(e.OrderId, e.Total, e.PlacedUtc, context.Metadata.OccurredUtc, ct);
        await AppendTimelineAsync(uow, e.OrderId, context, ct);
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
        var e = context.Event;
        await uow.ApplyShippedAsync(e.OrderId, e.ShippedUtc, context.Metadata.OccurredUtc, ct);
        await AppendTimelineAsync(uow, e.OrderId, context, ct);
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
        var e = context.Event;
        await uow.ApplyCancelledAsync(e.OrderId, e.CancelledUtc, context.Metadata.OccurredUtc, ct);
        await AppendTimelineAsync(uow, e.OrderId, context, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<OrderCompleted> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        await uow.ApplyCompletedAsync(e.OrderId, e.CompletedUtc, context.Metadata.OccurredUtc, ct);
        await AppendTimelineAsync(uow, e.OrderId, context, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<ShipmentScheduled> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        // ShipmentScheduled carries both ShipmentId and OrderId. Record the mapping
        // so the three shipment-update handlers (commit 17), which carry only
        // ShipmentId, can resolve the OrderId. See ADR 0020.
        await uow.InsertShipmentMappingAsync(
            new OrderDetailShipmentRow(e.ShipmentId, e.OrderId, e.ScheduledUtc), ct);
        await AppendTimelineAsync(uow, e.OrderId, context, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // One timeline entry per observed event (D1). event_type is the CLR type name,
    // equal to the event store's registered token by construction (the Postgres
    // EventTypeRegistry defaults to Type.Name and every provider registers bare
    // typeof(X)). occurred_utc and global_position come from the envelope, not the
    // payload. The payload is the event serialised by its runtime type with the
    // shared options, byte-identical to PostgresEventStore's payload column. ADR 0018.
    private Task AppendTimelineAsync<TEvent>(
        IOrderDetailUnitOfWork uow, Guid orderId, EventContext<TEvent> context, CancellationToken ct)
        where TEvent : IDomainEvent
        => uow.AppendTimelineAsync(
            new OrderDetailTimelineRow(
                OrderId: orderId,
                GlobalPosition: context.GlobalPosition,
                EventType: typeof(TEvent).Name,
                OccurredUtc: context.Metadata.OccurredUtc,
                Payload: JsonSerializer.Serialize(context.Event, context.Event.GetType(), _jsonOptions)),
            ct);
}
