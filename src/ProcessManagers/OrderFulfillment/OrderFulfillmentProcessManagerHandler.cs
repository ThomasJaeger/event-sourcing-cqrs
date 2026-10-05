using EventSourcingCqrs.Domain.Billing;
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

// Drives OrderFulfillmentProcessManager across its observed events. Forward
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
    IProcessManagerHandler<OrderCancelled>,
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

    // Declared on the handler contract (ADR 0042): the dispatcher reads it to build the caused-event
    // context this PM's own writes are stamped from, and the handler passes it to the caused bus for
    // the commands it dispatches. One identity, both uses.
    public SystemActor Actor => SystemActors.OrderFulfillment;

    private readonly ICausedCommandBus _bus;
    private readonly IProcessManagerRepository<OrderFulfillmentProcessManager> _pms;
    private readonly IEventStoreRepository<Order> _orders;
    private readonly IEventStoreRepository<Shipment> _shipments;
    private readonly ISkuToInventoryIdStore _skuLookup;
    private readonly OrderFulfillmentCompensation _compensation;
    private readonly IDelayQueue _delayQueue;
    private readonly IWorkflowLock _workflowLock;
    private readonly IEventStoreRepository<Payment> _payments;
    private readonly IEventStoreRepository<Inventory> _inventory;

    public OrderFulfillmentProcessManagerHandler(
        ICausedCommandBus bus,
        IProcessManagerRepository<OrderFulfillmentProcessManager> pms,
        IEventStoreRepository<Order> orders,
        IEventStoreRepository<Shipment> shipments,
        ISkuToInventoryIdStore skuLookup,
        OrderFulfillmentCompensation compensation,
        IDelayQueue delayQueue, IWorkflowLock workflowLock, IEventStoreRepository<Payment> payments,
        IEventStoreRepository<Inventory> inventory)
    {
        _bus = bus;
        _pms = pms;
        _orders = orders;
        _shipments = shipments;
        _skuLookup = skuLookup;
        _compensation = compensation;
        _delayQueue = delayQueue;
        _workflowLock = workflowLock;
        _payments = payments;
        _inventory = inventory;
    }

    // OrderPlaced opens the workflow: schedule the payment timeout and ask Billing
    // to authorize payment.
    public async Task HandleAsync(EventContext<OrderPlaced> context, CancellationToken ct)
    {
        var orderId = context.Event.OrderId;
        await _workflowLock.RunAsync(context.Metadata.Tenant, orderId,
            () => HandleCoreAsync(context, ct), ct);
    }

    private async Task HandleCoreAsync(EventContext<OrderPlaced> context, CancellationToken ct)
    {
        var e = context.Event;
        var stream = OrderFulfillmentStreams.For(context.Metadata.Tenant, e.OrderId);
        var pm = await _pms.LoadOrNewAsync(stream, OrderFulfillmentStreams.New, ct);

        if (pm.State == OrderFulfillmentState.NotStarted)
        {
            pm.Start(e.OrderId, e.Total, Guid.NewGuid());
            await ScheduleTimeoutAsync(
                stream, new TimeoutAwaitingPaymentForOrder(e.OrderId),
                OrderFulfillmentSteps.AwaitPaymentTimeout, context.Metadata, AwaitingPaymentTimeout, ct);
            await _pms.SaveAsync(pm, ct);
        }

        if (pm.CancellationReason is not null
            && pm.State is not (OrderFulfillmentState.Cancelled or OrderFulfillmentState.Completed))
        {
            await CancelAsync(pm, pm.CancellationReason, context.Metadata, ct);
            return;
        }
        var order = await _orders.LoadAsync(e.OrderId, ct);
        if (order?.Status == OrderStatus.Cancelled && pm.State == OrderFulfillmentState.AwaitingPayment)
        {
            await CancelAsync(pm, "Order cancelled by customer.", context.Metadata, ct);
            return;
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
        var orderId = context.Event.OrderId;
        await _workflowLock.RunAsync(context.Metadata.Tenant, orderId,
            () => HandleCoreAsync(context, ct), ct);
    }

    private async Task HandleCoreAsync(EventContext<PaymentAuthorized> context, CancellationToken ct)
    {
        var e = context.Event;
        var stream = OrderFulfillmentStreams.For(context.Metadata.Tenant, e.OrderId);
        var pm = await _pms.LoadAsync(stream, OrderFulfillmentStreams.New, ct)
            ?? throw new InvalidOperationException(
                $"OrderFulfillment PM {stream} not found handling PaymentAuthorized for order {e.OrderId}.");

        await _delayQueue.CancelAsync(
            stream, OrderFulfillmentSteps.AwaitPaymentTimeout, "Payment authorized.", ct);

        if (pm.State == OrderFulfillmentState.Cancelled)
        {
            var latePayment = await _payments.LoadAsync(e.PaymentId, ct);
            if (latePayment?.Status == PaymentStatus.Authorized)
                await _bus.SendAsync(new VoidPayment(e.PaymentId, "Late authorization after cancellation."),
                    context.Metadata, Actor,
                    IdempotencyKeys.ForProcessManager(stream, OrderFulfillmentSteps.VoidPayment), ct);
            return;
        }
        if (pm.CancellationReason is not null)
        {
            await _compensation.CompensateWithReleasesAsync(
                pm, pm.CancellationReason, context.Metadata, ct);
            return;
        }
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

        if (order?.Status == OrderStatus.Cancelled)
        {
            await CancelAsync(pm, "Order cancelled by customer.", context.Metadata, ct);
            return;
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
        var shipment = await _shipments.LoadAsync(context.Event.ShipmentId, ct)
            ?? throw new AggregateNotFoundException(context.Event.ShipmentId);
        var orderId = shipment.OrderId;
        await _workflowLock.RunAsync(context.Metadata.Tenant, orderId,
            () => HandleCoreAsync(context, ct), ct);
    }

    private async Task HandleCoreAsync(EventContext<ShipmentDispatched> context, CancellationToken ct)
    {
        var (pm, _) = await CorrelateByShipmentAsync(context.Event.ShipmentId, context.Metadata.Tenant, ct);
        await _delayQueue.CancelAsync(
            pm.StreamId, OrderFulfillmentSteps.AwaitDispatchTimeout, "Shipment dispatched.", ct);
        if (pm.CancellationReason is null && pm.State == OrderFulfillmentState.AwaitingDispatch)
        {
            pm.RecordShipmentDispatched();  // -> AwaitingDelivery
            await _pms.SaveAsync(pm, ct);
        }
    }

    public async Task HandleAsync(EventContext<ShipmentDelivered> context, CancellationToken ct)
    {
        var shipment = await _shipments.LoadAsync(context.Event.ShipmentId, ct)
            ?? throw new AggregateNotFoundException(context.Event.ShipmentId);
        var orderId = shipment.OrderId;
        await _workflowLock.RunAsync(context.Metadata.Tenant, orderId,
            () => HandleCoreAsync(context, ct), ct);
    }

    private async Task HandleCoreAsync(EventContext<ShipmentDelivered> context, CancellationToken ct)
    {
        var (pm, _) = await CorrelateByShipmentAsync(context.Event.ShipmentId, context.Metadata.Tenant, ct);
        if (pm.CancellationReason is null && pm.State == OrderFulfillmentState.AwaitingDelivery)
        {
            // Pattern A with an internal dispatch: record delivery, dispatch
            // MarkOrderCompleted, record the terminal, one save. MarkOrderCompleted
            // carries OrderId from the loaded Shipment, not a PM-minted id, so no
            // save-before-dispatch (R2) is needed; the single save closes the orphan
            // window and a redelivery re-dispatches on the mark-completed key.
            pm.RecordShipmentDelivered();
            await _bus.SendAsync(
                new MarkOrderCompleted(pm.OrderId),
                context.Metadata, Actor,
                IdempotencyKeys.ForProcessManager(pm.StreamId, OrderFulfillmentSteps.MarkCompleted), ct);
            pm.Complete();                  // -> Completed
            await _pms.SaveAsync(pm, ct);
        }
    }

    public Task HandleAsync(EventContext<OrderCancelled> context, CancellationToken ct)
        => _workflowLock.RunAsync(context.Metadata.Tenant, context.Event.OrderId, async () =>
        {
            var pm = await _pms.LoadAsync(OrderFulfillmentStreams.For(context.Metadata.Tenant,
                context.Event.OrderId), OrderFulfillmentStreams.New, ct);
            if (pm is null || pm.State is OrderFulfillmentState.Completed or OrderFulfillmentState.Cancelled)
                return;
            await CancelAsync(pm, context.Event.Reason, context.Metadata, ct);
        }, ct);

    private async Task CancelAsync(OrderFulfillmentProcessManager pm, string reason,
        EventMetadata metadata, CancellationToken ct)
    {
        reason = pm.CancellationReason ?? reason;
        // A reservation can commit before the fan-out outcomes are saved. Under
        // the order gate no new fan-out can race this reconciliation. Recover
        // those effects from Inventory before choosing the compensation set.
        if (pm.State == OrderFulfillmentState.AwaitingInventory)
        {
            var order = await _orders.LoadAsync(pm.OrderId, ct)
                ?? throw new AggregateNotFoundException(pm.OrderId);
            foreach (var line in order.Lines.Where(l => !pm.Reservations.ContainsKey(l.LineId)))
            {
                var inventoryId = await _skuLookup.GetInventoryIdAsync(line.Sku, ct);
                if (inventoryId is null) continue;
                var inventory = await _inventory.LoadAsync(inventoryId.Value, ct);
                var reserved = inventory?.Reservations.SingleOrDefault(
                    r => r.OrderId == pm.OrderId && r.LineId == line.LineId);
                if (reserved is not null)
                {
                    pm.RecordLineReserved(line.LineId, reserved.Sku, reserved.Quantity, inventoryId.Value);
                    await OrderFulfillmentPersistence.SaveBatchIfFullAsync(pm, _pms, ct);
                }
            }
            // Persist recovery before releasing anything: a retry must still
            // know which releases succeeded before a later compensation failed.
            await _pms.SaveAsync(pm, ct);
        }
        var payment = await _payments.LoadAsync(pm.PaymentId, ct);
        if (payment is null)
            await _compensation.CompensateAuthorizeFailureAsync(pm, reason, metadata, ct);
        else
            await _compensation.CompensateWithReleasesAsync(pm, reason, metadata, ct);
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
        // An outcome already saved is final for this attempt. In particular,
        // retrying a failed line could reserve stock that compensation cannot see.
        var pending = order.Lines.Where(line => !pm.Reservations.ContainsKey(line.LineId));
        var results = await Task.WhenAll(pending.Select(async line =>
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

        // Persist bounded batches of outcomes. A retry dispatches only lines
        // whose outcomes were not saved, using their original command keys.
        foreach (var (line, inventoryId, outcome) in results)
        {
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
            await OrderFulfillmentPersistence.SaveBatchIfFullAsync(pm, _pms, ct);
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
        Guid shipmentId, TenantId tenant, CancellationToken ct)
    {
        var shipment = await _shipments.LoadAsync(shipmentId, ct)
            ?? throw new InvalidOperationException(
                $"Shipment {shipmentId} not found correlating a shipment event.");
        var stream = OrderFulfillmentStreams.For(tenant, shipment.OrderId);
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
