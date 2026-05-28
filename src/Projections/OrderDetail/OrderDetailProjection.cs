using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using Microsoft.Extensions.Logging;

namespace EventSourcingCqrs.Projections.OrderDetail;

// Pattern from Chapter 13: the order detail read model. Subscribes to all sixteen
// events across three contexts (eight Sales, four Fulfillment shipment, four Billing)
// and maintains a header row, line items, and a JSONB event timeline with one entry
// per observed event.
//
// Every handler reads the checkpoint, applies its writes, appends one timeline entry,
// and commits in one transaction; the skip-guard makes redelivery a no-op. The events
// that carry OrderId on the payload (the eight Sales events, ShipmentScheduled, and
// PaymentAuthorized) key the read model directly. The six that carry only their own
// aggregate's id (the three shipment-update events and the three OrderId-less payment
// events) resolve OrderId through the projection-private lookups recorded by
// ShipmentScheduled and PaymentAuthorized; a missing mapping no-ops with a debug log
// and still advances the checkpoint. See ADR 0020.
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
      IEventHandler<ShipmentScheduled>,
      IEventHandler<ShipmentDispatched>,
      IEventHandler<ShipmentDelivered>,
      IEventHandler<ShipmentReturned>,
      IEventHandler<PaymentAuthorized>,
      IEventHandler<PaymentCaptured>,
      IEventHandler<PaymentRefunded>,
      IEventHandler<PaymentVoided>
{
    public string Name => "order-detail";

    private readonly IOrderDetailStore _store;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<OrderDetailProjection> _logger;

    // The shared JsonSerializerOptions singleton (registered by AddPostgresEventStore)
    // is injected so the timeline payload serialises byte-identically to the event
    // store's own payload column. The logger backs the six not-found debug branches.
    public OrderDetailProjection(
        IOrderDetailStore store,
        JsonSerializerOptions jsonOptions,
        ILogger<OrderDetailProjection> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _jsonOptions = jsonOptions;
        _logger = logger;
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
        StageNotification(uow, e.OrderId, nameof(OrderDrafted));
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
        StageNotification(uow, e.OrderId, nameof(OrderLineAdded));
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
        StageNotification(uow, e.OrderId, nameof(OrderLineRemoved));
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
        StageNotification(uow, e.OrderId, nameof(ShippingAddressSet));
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
        StageNotification(uow, e.OrderId, nameof(OrderPlaced));
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
        StageNotification(uow, e.OrderId, nameof(OrderShipped));
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
        StageNotification(uow, e.OrderId, nameof(OrderCancelled));
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
        StageNotification(uow, e.OrderId, nameof(OrderCompleted));
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
        StageNotification(uow, e.OrderId, nameof(ShipmentScheduled));
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // ShipmentDispatched carries only ShipmentId. Resolve OrderId through the mapping
    // ShipmentScheduled recorded, then append the timeline entry. It owns no header
    // column. An unmapped shipment no-ops with a debug log; the checkpoint advances.
    public async Task HandleAsync(EventContext<ShipmentDispatched> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        var orderId = await uow.GetOrderIdByShipmentIdAsync(e.ShipmentId, ct);
        if (orderId is null)
        {
            _logger.LogDebug(
                "ShipmentDispatched for shipment {ShipmentId} has no order_detail_shipments " +
                "mapping; skipping timeline append.",
                e.ShipmentId);
        }
        else
        {
            await AppendTimelineAsync(uow, orderId.Value, context, ct);
            StageNotification(uow, orderId.Value, nameof(ShipmentDispatched));
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<ShipmentDelivered> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        var orderId = await uow.GetOrderIdByShipmentIdAsync(e.ShipmentId, ct);
        if (orderId is null)
        {
            _logger.LogDebug(
                "ShipmentDelivered for shipment {ShipmentId} has no order_detail_shipments " +
                "mapping; skipping timeline append.",
                e.ShipmentId);
        }
        else
        {
            await AppendTimelineAsync(uow, orderId.Value, context, ct);
            StageNotification(uow, orderId.Value, nameof(ShipmentDelivered));
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // ShipmentReturned resolves OrderId through the mapping, marks the header
    // returned (returned_utc only; the Sales status stays as it was, D5), and appends
    // the timeline entry. An unmapped shipment no-ops with a debug log.
    public async Task HandleAsync(EventContext<ShipmentReturned> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        var orderId = await uow.GetOrderIdByShipmentIdAsync(e.ShipmentId, ct);
        if (orderId is null)
        {
            _logger.LogDebug(
                "ShipmentReturned for shipment {ShipmentId} has no order_detail_shipments " +
                "mapping; skipping return-state update.",
                e.ShipmentId);
        }
        else
        {
            await uow.MarkReturnedAsync(
                orderId.Value, e.ReturnedUtc, context.Metadata.OccurredUtc, ct);
            await AppendTimelineAsync(uow, orderId.Value, context, ct);
            StageNotification(uow, orderId.Value, nameof(ShipmentReturned));
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // PaymentAuthorized carries both PaymentId and OrderId. Record the mapping so the
    // three OrderId-less payment events can resolve their order, then append the
    // timeline entry. Payment state lives in the timeline only for v1; no header
    // column changes.
    public async Task HandleAsync(EventContext<PaymentAuthorized> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        await uow.InsertPaymentMappingAsync(
            new OrderDetailPaymentRow(e.PaymentId, e.OrderId, e.AuthorizedUtc), ct);
        await AppendTimelineAsync(uow, e.OrderId, context, ct);
        StageNotification(uow, e.OrderId, nameof(PaymentAuthorized));
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<PaymentCaptured> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        var orderId = await uow.GetOrderIdByPaymentIdAsync(e.PaymentId, ct);
        if (orderId is null)
        {
            _logger.LogDebug(
                "PaymentCaptured for payment {PaymentId} has no order_detail_payments " +
                "mapping; skipping timeline append.",
                e.PaymentId);
        }
        else
        {
            await AppendTimelineAsync(uow, orderId.Value, context, ct);
            StageNotification(uow, orderId.Value, nameof(PaymentCaptured));
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<PaymentRefunded> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        var orderId = await uow.GetOrderIdByPaymentIdAsync(e.PaymentId, ct);
        if (orderId is null)
        {
            _logger.LogDebug(
                "PaymentRefunded for payment {PaymentId} has no order_detail_payments " +
                "mapping; skipping timeline append.",
                e.PaymentId);
        }
        else
        {
            await AppendTimelineAsync(uow, orderId.Value, context, ct);
            StageNotification(uow, orderId.Value, nameof(PaymentRefunded));
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<PaymentVoided> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        var orderId = await uow.GetOrderIdByPaymentIdAsync(e.PaymentId, ct);
        if (orderId is null)
        {
            _logger.LogDebug(
                "PaymentVoided for payment {PaymentId} has no order_detail_payments " +
                "mapping; skipping timeline append.",
                e.PaymentId);
        }
        else
        {
            await AppendTimelineAsync(uow, orderId.Value, context, ct);
            StageNotification(uow, orderId.Value, nameof(PaymentVoided));
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // Stages an order notification for the SignalR hub, keyed by OrderId for the
    // order:{orderId} group (D2). OrderDetail is a v1 subscriber. The widget set is
    // empty for now: the page re-queries authoritative state on any notification
    // (D1), and the precise Chapter 13 widget vocabulary lands with the Cluster 2
    // retrofit pages. Called only on branches that change a row; the unmapped
    // lookup branches stage nothing.
    private void StageNotification(IOrderDetailUnitOfWork uow, Guid orderId, string eventName)
        => uow.PublishOnCommit(new NotificationEnvelope(Name, orderId.ToString(), eventName, []));

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
