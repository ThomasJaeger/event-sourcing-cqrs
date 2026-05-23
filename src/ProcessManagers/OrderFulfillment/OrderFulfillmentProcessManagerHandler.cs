using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// Drives OrderFulfillmentProcessManager across its four observed events. Each
// handler follows the R2 ordering (ADR 0015 editorial, F-0009-N): record the
// transition and save before dispatching the command that carries any minted id,
// so a redelivery reloads the same id and re-dispatches idempotently. Reservation
// outcomes are the exception, recorded after dispatch because the outcome is the
// dispatch result. The PM learns reservation results from the fan-out's
// CommandOutcome, not from InventoryReserved (Decision 10, F-0009-M).
//
// Not DI-registered here; registration and dispatcher routing land at commit 27.
// Compensation (branches 1-3) lands at commits 23-24; MarkOrderCompleted at 25.
public sealed class OrderFulfillmentProcessManagerHandler :
    IProcessManagerHandler<OrderPlaced>,
    IProcessManagerHandler<PaymentAuthorized>,
    IProcessManagerHandler<ShipmentDispatched>,
    IProcessManagerHandler<ShipmentDelivered>
{
    // Phase 7's UI replaces this with the customer's real payment-method
    // reference. Payment.Authorize rejects null/whitespace, so the placeholder
    // must be non-empty; this satisfies it. No IValidator<AuthorizePayment> is
    // registered, so nothing else constrains it.
    private const string SystemOrchestratedPaymentMethod = "system-orchestrated";

    private static readonly SystemActor Actor = SystemActors.OrderFulfillment;

    private static class Steps
    {
        public const string AuthorizePayment = "authorize-payment";
        public const string Reserve = "reserve";
        public const string ScheduleShipment = "schedule-shipment";
    }

    private readonly ICausedCommandBus _bus;
    private readonly IProcessManagerRepository<OrderFulfillmentProcessManager> _pms;
    private readonly IEventStoreRepository<Order> _orders;
    private readonly IEventStoreRepository<Shipment> _shipments;
    private readonly ISkuToInventoryIdStore _skuLookup;

    public OrderFulfillmentProcessManagerHandler(
        ICausedCommandBus bus,
        IProcessManagerRepository<OrderFulfillmentProcessManager> pms,
        IEventStoreRepository<Order> orders,
        IEventStoreRepository<Shipment> shipments,
        ISkuToInventoryIdStore skuLookup)
    {
        _bus = bus;
        _pms = pms;
        _orders = orders;
        _shipments = shipments;
        _skuLookup = skuLookup;
    }

    private static StreamId PmStream(Guid orderId) =>
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, orderId);

    private static OrderFulfillmentProcessManager Factory(StreamId streamId) => new(streamId);

    // OrderPlaced opens the workflow and asks Billing to authorize payment.
    public async Task HandleAsync(EventContext<OrderPlaced> context, CancellationToken ct)
    {
        var e = context.Event;
        var stream = PmStream(e.OrderId);
        var pm = await _pms.LoadOrNewAsync(stream, Factory, ct);

        if (pm.State == OrderFulfillmentState.NotStarted)
        {
            // Mint the PaymentId and persist it before the command that carries
            // it dispatches: a redelivery reloads it rather than minting anew.
            pm.Start(e.OrderId, e.Total, Guid.NewGuid());
            await _pms.SaveAsync(pm, ct);
        }

        await _bus.TrySendAsync(
            new AuthorizePayment(pm.PaymentId, e.OrderId, pm.OrderTotal!, SystemOrchestratedPaymentMethod),
            context.Metadata,
            Actor,
            IdempotencyKeys.ForProcessManager(stream, Steps.AuthorizePayment),
            ct);
        // Success leaves the PM AwaitingPayment, awaiting PaymentAuthorized.
        // A failed authorization routes to branch-1 compensation (commit 23).
    }

    // PaymentAuthorized records the authorization, fans out one ReserveInventory
    // per order line, and on full success requests shipment scheduling.
    public async Task HandleAsync(EventContext<PaymentAuthorized> context, CancellationToken ct)
    {
        var e = context.Event;
        var stream = PmStream(e.OrderId);
        var pm = await _pms.LoadAsync(stream, Factory, ct)
            ?? throw new InvalidOperationException(
                $"OrderFulfillment PM {stream} not found handling PaymentAuthorized for order {e.OrderId}.");

        if (pm.State == OrderFulfillmentState.AwaitingPayment)
        {
            pm.RecordPaymentAuthorized();   // -> AwaitingInventory
            await _pms.SaveAsync(pm, ct);
        }

        Order? order = null;
        if (pm.State is OrderFulfillmentState.AwaitingInventory or OrderFulfillmentState.AwaitingDispatch)
        {
            order = await _orders.LoadAsync(e.OrderId, ct)
                ?? throw new InvalidOperationException(
                    $"Order {e.OrderId} not found handling PaymentAuthorized.");
        }

        if (pm.State == OrderFulfillmentState.AwaitingInventory)
        {
            await FanOutReservationsAsync(pm, order!, context.Metadata, stream, ct);

            if (AllLinesReserved(pm, order!))
            {
                pm.CompleteReservations();                     // -> AwaitingShipment
                pm.RequestShipmentScheduling(Guid.NewGuid());  // mint+persist ShipmentId, -> AwaitingDispatch
                await _pms.SaveAsync(pm, ct);                  // saved BEFORE the ScheduleShipment dispatch
            }
            // Partial or total reservation failure leaves the PM at
            // AwaitingInventory; compensation lands at commits 23-24.
        }

        if (pm.State == OrderFulfillmentState.AwaitingDispatch)
        {
            var lines = order!.Lines
                .Select(l => new ShipmentLine(e.OrderId, l.LineId, l.Sku, l.Quantity))
                .ToList();
            await _bus.TrySendAsync(
                new ScheduleShipment(pm.ShipmentId, e.OrderId, order.ShippingAddress!, lines),
                context.Metadata,
                Actor,
                IdempotencyKeys.ForProcessManager(stream, Steps.ScheduleShipment),
                ct);
        }
    }

    // ShipmentDispatched carries no OrderId, so the Shipment is loaded to recover
    // it (ADR 0015 cross-aggregate read), then the PM is found and advanced.
    public async Task HandleAsync(EventContext<ShipmentDispatched> context, CancellationToken ct)
    {
        var (pm, _) = await CorrelateByShipmentAsync(context.Event.ShipmentId, ct);
        if (pm.State == OrderFulfillmentState.AwaitingDispatch)
        {
            pm.RecordShipmentDispatched();  // -> AwaitingDelivery
            await _pms.SaveAsync(pm, ct);
        }
        // No command dispatch.
    }

    public async Task HandleAsync(EventContext<ShipmentDelivered> context, CancellationToken ct)
    {
        var (pm, _) = await CorrelateByShipmentAsync(context.Event.ShipmentId, ct);
        if (pm.State == OrderFulfillmentState.AwaitingDelivery)
        {
            // Commit 21 records delivery and completes in one transition pair.
            // Commit 25 splits this: record delivery, save, dispatch
            // MarkOrderCompleted, then Complete and save.
            pm.RecordShipmentDelivered();
            pm.Complete();                  // -> Completed
            await _pms.SaveAsync(pm, ct);
        }
    }

    private async Task FanOutReservationsAsync(
        OrderFulfillmentProcessManager pm,
        Order order,
        EventMetadata causing,
        StreamId stream,
        CancellationToken ct)
    {
        // Parallel dispatch, one ReserveInventory per line, latency bounded by the
        // slowest single reservation rather than the line count (Decision 10).
        var results = await Task.WhenAll(order.Lines.Select(async line =>
        {
            var inventoryId = await _skuLookup.GetInventoryIdAsync(line.Sku, ct);
            if (inventoryId is null)
            {
                return (line, inventoryId, outcome: (CommandOutcome?)null);
            }

            var outcome = await _bus.TrySendAsync(
                new ReserveInventory(inventoryId.Value, order.Id, line.LineId, line.Quantity),
                causing,
                Actor,
                IdempotencyKeys.ForProcessManager(stream, Steps.Reserve, line.LineId),
                ct);
            return (line, inventoryId, outcome: (CommandOutcome?)outcome);
        }));

        // End-of-fan-out save: record every outcome once, then a single save. A
        // line already recorded on an earlier delivery is skipped, so the keyed
        // re-dispatch above stays the only redelivery cost.
        foreach (var (line, inventoryId, outcome) in results)
        {
            if (pm.Reservations.ContainsKey(line.LineId))
            {
                continue;
            }

            if (inventoryId is null)
            {
                pm.RecordLineReservationFailed(
                    line.LineId, line.Sku, line.Quantity, $"No inventory mapping for SKU {line.Sku}.");
            }
            else if (outcome!.IsSuccess)
            {
                pm.RecordLineReserved(line.LineId, line.Sku, line.Quantity, inventoryId.Value);
            }
            else
            {
                pm.RecordLineReservationFailed(
                    line.LineId, line.Sku, line.Quantity, outcome.Failure!.Message);
            }
        }

        await _pms.SaveAsync(pm, ct);   // no-ops when every line was already recorded
    }

    private static bool AllLinesReserved(OrderFulfillmentProcessManager pm, Order order) =>
        pm.Reservations.Count == order.Lines.Count
        && pm.Reservations.Values.All(r => r.Status == ReservationLineStatus.Reserved);

    // The shipment events carry no OrderId. Loading the Shipment recovers it,
    // and the PM's tracked ShipmentId guards against an event for a shipment this
    // PM does not own. A clear error on that anomaly beats a silent no-op.
    private async Task<(OrderFulfillmentProcessManager Pm, Shipment Shipment)> CorrelateByShipmentAsync(
        Guid shipmentId, CancellationToken ct)
    {
        var shipment = await _shipments.LoadAsync(shipmentId, ct)
            ?? throw new InvalidOperationException(
                $"Shipment {shipmentId} not found correlating a shipment event.");
        var stream = PmStream(shipment.OrderId);
        var pm = await _pms.LoadAsync(stream, Factory, ct)
            ?? throw new InvalidOperationException(
                $"OrderFulfillment PM {stream} not found for shipment {shipmentId}.");
        if (pm.ShipmentId != shipmentId)
        {
            throw new InvalidOperationException(
                $"OrderFulfillment PM {stream} tracks shipment {pm.ShipmentId}, not {shipmentId}.");
        }

        return (pm, shipment);
    }
}
