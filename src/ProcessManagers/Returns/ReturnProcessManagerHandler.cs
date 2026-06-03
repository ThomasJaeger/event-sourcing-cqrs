using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.ReadModels;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;

namespace EventSourcingCqrs.ProcessManagers.Returns;

// Drives ReturnProcessManager on ShipmentReturned. ShipmentReturned carries only a
// ShipmentId, so the handler loads the Shipment to recover the OrderId and the
// returned lines, then restocks each line and voids the payment. PaymentId comes
// from the OrderIdToPaymentId read model, not the OrderFulfillment PM's state:
// cross-PM data acquisition is contract-mediated (commit 26a). Pattern A
// throughout, terminal-guarded for redelivery. Any step failure routes to Stuck;
// there is no compensation (Decision 12).
//
// Not DI-registered here; registration and dispatcher routing land at commit 27.
public sealed class ReturnProcessManagerHandler : IProcessManagerHandler<ShipmentReturned>
{
    private static readonly SystemActor Actor = SystemActors.Return;

    private readonly ICausedCommandBus _bus;
    private readonly IProcessManagerRepository<ReturnProcessManager> _pms;
    private readonly IEventStoreRepository<Shipment> _shipments;
    private readonly ISkuToInventoryIdStore _skuLookup;
    private readonly IOrderIdToPaymentIdStore _paymentLookup;

    public ReturnProcessManagerHandler(
        ICausedCommandBus bus,
        IProcessManagerRepository<ReturnProcessManager> pms,
        IEventStoreRepository<Shipment> shipments,
        ISkuToInventoryIdStore skuLookup,
        IOrderIdToPaymentIdStore paymentLookup)
    {
        _bus = bus;
        _pms = pms;
        _shipments = shipments;
        _skuLookup = skuLookup;
        _paymentLookup = paymentLookup;
    }

    public async Task HandleAsync(EventContext<ShipmentReturned> context, CancellationToken ct)
    {
        var e = context.Event;
        var shipment = await _shipments.LoadAsync(e.ShipmentId, ct)
            ?? throw new InvalidOperationException(
                $"Shipment {e.ShipmentId} not found handling ShipmentReturned.");
        var stream = ReturnStreams.For(shipment.OrderId);
        var pm = await _pms.LoadOrNewAsync(stream, ReturnStreams.New, ct);

        if (pm.State is ReturnState.Completed or ReturnState.Stuck)
        {
            // Terminal: redelivery, or a re-return of an already-handled shipment.
            return;
        }

        if (pm.State == ReturnState.NotStarted)
        {
            pm.Start(shipment.OrderId);   // -> RestockingInventory
            await _pms.SaveAsync(pm, ct);
        }

        if (pm.State == ReturnState.RestockingInventory)
        {
            if (!await RestockAsync(pm, shipment, context.Metadata, stream, ct))
            {
                return;   // a line failed; MarkStuck saved inside RestockAsync
            }
            pm.RecordRestock();           // -> VoidingPayment
            await _pms.SaveAsync(pm, ct);
        }

        if (pm.State == ReturnState.VoidingPayment)
        {
            var paymentId = await _paymentLookup.GetPaymentIdAsync(shipment.OrderId, ct);
            if (paymentId is null)
            {
                pm.MarkStuck($"No payment mapping for order {shipment.OrderId}.");
                await _pms.SaveAsync(pm, ct);
                return;
            }

            var outcome = await _bus.TrySendAsync(
                new VoidPayment(paymentId.Value, $"Void for returned order {shipment.OrderId}."),
                context.Metadata, Actor,
                IdempotencyKeys.ForProcessManager(stream, ReturnSteps.VoidPayment), ct);
            if (!outcome.IsSuccess)
            {
                pm.MarkStuck(
                    $"Void payment failed for order {shipment.OrderId}: {outcome.Failure!.Message}");
                await _pms.SaveAsync(pm, ct);
                return;
            }

            pm.RecordVoid();
            pm.Complete();                // -> Completed
            await _pms.SaveAsync(pm, ct);
        }
    }

    // Restocks each returned line in parallel. Returns true when every line
    // succeeded; on any failure it marks the PM Stuck (saved) and returns false.
    // A mid-fan-out failure leaves prior successful AdjustInventory dispatches in
    // place: there is no de-restock, so the Stuck reason names the failed lines and
    // how many were restocked, and operational tooling triages from there.
    private async Task<bool> RestockAsync(
        ReturnProcessManager pm,
        Shipment shipment,
        EventMetadata causing,
        StreamId stream,
        CancellationToken ct)
    {
        var results = await Task.WhenAll(shipment.Lines.Select(async line =>
        {
            var inventoryId = await _skuLookup.GetInventoryIdAsync(line.Sku, causing.Tenant, ct);
            if (inventoryId is null)
            {
                return (line, inventoryId, outcome: (CommandOutcome?)null);
            }

            var outcome = await _bus.TrySendAsync(
                new AdjustInventory(
                    inventoryId.Value, line.Quantity, $"Restock for returned order {shipment.OrderId}."),
                causing, Actor,
                IdempotencyKeys.ForProcessManager(stream, ReturnSteps.Restock, line.LineId), ct);
            return (line, inventoryId, outcome: (CommandOutcome?)outcome);
        }));

        var failures = results
            .Where(r => r.inventoryId is null || !r.outcome!.IsSuccess)
            .ToList();
        if (failures.Count == 0)
        {
            return true;
        }

        var detail = string.Join("; ", failures.Select(r => r.inventoryId is null
            ? $"line {r.line.LineId:N}: no inventory mapping for SKU {r.line.Sku}"
            : $"line {r.line.LineId:N}: {r.outcome!.Failure!.Message}"));
        var restocked = results.Length - failures.Count;
        pm.MarkStuck(
            $"Restock for order {shipment.OrderId} could not complete: {detail}. " +
            $"{restocked} line(s) were restocked and are not reversed.");
        await _pms.SaveAsync(pm, ct);
        return false;
    }
}
