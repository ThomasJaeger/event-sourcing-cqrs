using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// Drives OrderFulfillmentProcessManager across its four observed events. Forward
// dispatches follow R2 ordering (ADR 0015 editorial, F-0009-N): record-and-save a
// minted id before the command that carries it dispatches. Reservation outcomes
// are recorded after dispatch, since the outcome is the dispatch result; the PM
// learns them from the fan-out's CommandOutcome, not from InventoryReserved
// (Decision 10, F-0009-M). Compensation lives in OrderFulfillmentCompensation, a
// collaborator shared with the timeout command handlers (commit 24).
//
// Timeout scheduling and cancellation follow the asymmetry the hook table makes
// legible: schedule inside the state-guarded transition (it opens with the
// recorded transition and persists with its save, one per state per PM lifetime);
// cancel unconditional on every delivery (idempotent, and outside-the-guard
// placement closes the crash-between-save-and-cancel window).
//
// Not DI-registered here; registration and dispatcher routing land at commit 27.
public sealed class OrderFulfillmentProcessManagerHandler :
    IProcessManagerHandler<OrderPlaced>,
    IProcessManagerHandler<PaymentAuthorized>,
    IProcessManagerHandler<ShipmentDispatched>,
    IProcessManagerHandler<ShipmentDelivered>
{
    // Phase 7's UI replaces this with the customer's real payment-method reference.
    // Payment.Authorize rejects null/whitespace, so the placeholder must be
    // non-empty; this satisfies it.
    private const string SystemOrchestratedPaymentMethod = "system-orchestrated";

    // Illustrative timeout windows. A real deployment would configure these.
    // fireAt derives from the inbound event's OccurredUtc, so no clock is injected.
    private static readonly TimeSpan AwaitingPaymentTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan AwaitingDispatchTimeout = TimeSpan.FromDays(2);

    private static readonly SystemActor Actor = SystemActors.OrderFulfillment;

    private readonly ICausedCommandBus _bus;
    private readonly IProcessManagerRepository<OrderFulfillmentProcessManager> _pms;
    private readonly IEventStoreRepository<Order> _orders;
    private readonly IEventStoreRepository<Shipment> _shipments;
    private readonly ISkuToInventoryIdStore _skuLookup;
    private readonly OrderFulfillmentCompensation _compensation;
    private readonly IDelayQueue _delayQueue;

    public OrderFulfillmentProcessManagerHandler(
        ICausedCommandBus bus,
        IProcessManagerRepository<OrderFulfillmentProcessManager> pms,
        IEventStoreRepository<Order> orders,
        IEventStoreRepository<Shipment> shipments,
        ISkuToInventoryIdStore skuLookup,
        OrderFulfillmentCompensation compensation,
        IDelayQueue delayQueue)
    {
        _bus = bus;
        _pms = pms;
        _orders = orders;
        _shipments = shipments;
        _skuLookup = skuLookup;
        _compensation = compensation;
        _delayQueue = delayQueue;
    }

    // OrderPlaced opens the workflow: schedule the payment timeout and ask Billing
    // to authorize payment.
    public async Task HandleAsync(EventContext<OrderPlaced> context, CancellationToken ct)
    {
        var e = context.Event;
        var stream = OrderFulfillmentStreams.For(e.OrderId);
        var pm = await _pms.LoadOrNewAsync(stream, OrderFulfillmentStreams.New, ct);

        if (pm.State == OrderFulfillmentState.NotStarted)
        {
            pm.Start(e.OrderId, e.Total, Guid.NewGuid());
            await ScheduleTimeoutAsync(
                stream, new TimeoutAwaitingPaymentForOrder(e.OrderId),
                OrderFulfillmentSteps.AwaitPaymentTimeout, context.Metadata, AwaitingPaymentTimeout, ct);
            await _pms.SaveAsync(pm, ct);
        }

        if (pm.State == OrderFulfillmentState.AwaitingPayment)
        {
            var outcome = await _bus.TrySendAsync(
                new AuthorizePayment(pm.PaymentId, e.OrderId, pm.OrderTotal!, SystemOrchestratedPaymentMethod),
                context.Metadata, Actor,
                IdempotencyKeys.ForProcessManager(stream, OrderFulfillmentSteps.AuthorizePayment), ct);
            if (!outcome.IsSuccess)
            {
                // Branch 1: the payment was never authorized.
                await _compensation.CompensateAuthorizeFailureAsync(
                    pm, outcome.Failure!.Message, context.Metadata, ct);
            }
        }
    }

    // PaymentAuthorized cancels the payment timeout, records the authorization,
    // fans out one ReserveInventory per line, and on full success schedules the
    // dispatch timeout and asks for shipment scheduling.
    public async Task HandleAsync(EventContext<PaymentAuthorized> context, CancellationToken ct)
    {
        var e = context.Event;
        var stream = OrderFulfillmentStreams.For(e.OrderId);
        var pm = await _pms.LoadAsync(stream, OrderFulfillmentStreams.New, ct)
            ?? throw new InvalidOperationException(
                $"OrderFulfillment PM {stream} not found handling PaymentAuthorized for order {e.OrderId}.");

        await _delayQueue.CancelAsync(
            stream, OrderFulfillmentSteps.AwaitPaymentTimeout, "Payment authorized.", ct);

        if (pm.State == OrderFulfillmentState.AwaitingPayment)
        {
            pm.RecordPaymentAuthorized();
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
                pm.CompleteReservations();
                pm.RequestShipmentScheduling(Guid.NewGuid());
                await ScheduleTimeoutAsync(
                    stream, new TimeoutAwaitingDispatchForOrder(e.OrderId),
                    OrderFulfillmentSteps.AwaitDispatchTimeout, context.Metadata, AwaitingDispatchTimeout, ct);
                await _pms.SaveAsync(pm, ct);
            }
            else
            {
                // Branches 2/3: zero or partial reservation. CompensateWithReleases
                // releases whatever reserved (none for the all-failed case), voids,
                // cancels.
                await _compensation.CompensateWithReleasesAsync(
                    pm, $"Inventory reservations could not be completed for order {e.OrderId}.",
                    context.Metadata, ct);
            }
        }

        if (pm.State == OrderFulfillmentState.AwaitingDispatch)
        {
            var lines = order!.Lines
                .Select(l => new ShipmentLine(e.OrderId, l.LineId, l.Sku, l.Quantity))
                .ToList();
            var outcome = await _bus.TrySendAsync(
                new ScheduleShipment(pm.ShipmentId, e.OrderId, order.ShippingAddress!, lines),
                context.Metadata, Actor,
                IdempotencyKeys.ForProcessManager(stream, OrderFulfillmentSteps.ScheduleShipment), ct);
            if (!outcome.IsSuccess)
            {
                // Branch 3 (shipment-scheduling failure): release the reserved
                // lines, void, cancel.
                await _compensation.CompensateWithReleasesAsync(
                    pm, outcome.Failure!.Message, context.Metadata, ct);
            }
        }
    }

    public async Task HandleAsync(EventContext<ShipmentDispatched> context, CancellationToken ct)
    {
        var (pm, _) = await CorrelateByShipmentAsync(context.Event.ShipmentId, ct);
        await _delayQueue.CancelAsync(
            pm.StreamId, OrderFulfillmentSteps.AwaitDispatchTimeout, "Shipment dispatched.", ct);
        if (pm.State == OrderFulfillmentState.AwaitingDispatch)
        {
            pm.RecordShipmentDispatched();  // -> AwaitingDelivery
            await _pms.SaveAsync(pm, ct);
        }
    }

    public async Task HandleAsync(EventContext<ShipmentDelivered> context, CancellationToken ct)
    {
        var (pm, _) = await CorrelateByShipmentAsync(context.Event.ShipmentId, ct);
        if (pm.State == OrderFulfillmentState.AwaitingDelivery)
        {
            // Pattern A with an internal dispatch: record delivery, dispatch
            // MarkOrderCompleted, record the terminal, one save. MarkOrderCompleted
            // carries OrderId from the loaded Shipment, not a PM-minted id, so no
            // save-before-dispatch (R2) is needed; the single save closes the orphan
            // window and a redelivery re-dispatches on the mark-completed key.
            pm.RecordShipmentDelivered();
            await _bus.TrySendAsync(
                new MarkOrderCompleted(pm.OrderId),
                context.Metadata, Actor,
                IdempotencyKeys.ForProcessManager(pm.StreamId, OrderFulfillmentSteps.MarkCompleted), ct);
            pm.Complete();                  // -> Completed
            await _pms.SaveAsync(pm, ct);
        }
    }

    private Task ScheduleTimeoutAsync(
        StreamId stream, ICommand command, string step, EventMetadata causing, TimeSpan after, CancellationToken ct)
        => _delayQueue.ScheduleAsync(
            command,
            new DateTimeOffset(causing.OccurredUtc, TimeSpan.Zero) + after,
            stream, step, causing, Actor,
            IdempotencyKeys.ForProcessManager(stream, step), ct);

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
                IdempotencyKeys.ForProcessManager(stream, OrderFulfillmentSteps.Reserve, line.LineId),
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

    // The shipment events carry no OrderId. Loading the Shipment recovers it, and
    // the PM's tracked ShipmentId guards against an event for a shipment this PM
    // does not own. A clear error on that anomaly beats a silent no-op.
    private async Task<(OrderFulfillmentProcessManager Pm, Shipment Shipment)> CorrelateByShipmentAsync(
        Guid shipmentId, CancellationToken ct)
    {
        var shipment = await _shipments.LoadAsync(shipmentId, ct)
            ?? throw new InvalidOperationException(
                $"Shipment {shipmentId} not found correlating a shipment event.");
        var stream = OrderFulfillmentStreams.For(shipment.OrderId);
        var pm = await _pms.LoadAsync(stream, OrderFulfillmentStreams.New, ct)
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
