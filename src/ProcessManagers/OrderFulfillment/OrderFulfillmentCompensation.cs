using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// The compensation routines for OrderFulfillmentProcessManager, extracted as a
// shared collaborator (commit 24) so both the PM handler (dispatch-failure
// triggers) and the timeout command handlers (timeout triggers) call the same
// code. Two routines map Decision 11's four branches: CompensateAuthorizeFailure
// is branch 1 (cancel the order, nothing to void, the payment was never
// authorized); CompensateWithReleases is branches 2, 3, and 4-collapsed (release
// whatever is reserved, void the authorization, cancel the order). Branch 2 is
// the empty-releases case: no line reserved means no ReleaseInventory dispatch and
// the state skips ReleasingInventory.
//
// Pattern A throughout: record and dispatch inside the caller's state guard, the
// terminal save last. Each routine also cancels the timeout the compensated-from
// state had scheduled, idempotently: an active cancel on the dispatch-failure
// path, a benign no-op when the timeout already fired (it is the trigger) or was
// never scheduled (the AwaitingInventory entries into CompensateWithReleases).
public sealed class OrderFulfillmentCompensation
{
    private static readonly SystemActor Actor = SystemActors.OrderFulfillment;

    private readonly ICausedCommandBus _bus;
    private readonly IProcessManagerRepository<OrderFulfillmentProcessManager> _pms;
    private readonly IDelayQueue _delayQueue;

    public OrderFulfillmentCompensation(
        ICausedCommandBus bus,
        IProcessManagerRepository<OrderFulfillmentProcessManager> pms,
        IDelayQueue delayQueue)
    {
        _bus = bus;
        _pms = pms;
        _delayQueue = delayQueue;
    }

    // Branch 1: AuthorizePayment failed (or the AwaitingPayment timeout fired), so
    // the payment was never authorized. Cancel the order; there is nothing to void.
    public async Task CompensateAuthorizeFailureAsync(
        OrderFulfillmentProcessManager pm, string reason, EventMetadata causing, CancellationToken ct)
    {
        await _delayQueue.CancelAsync(pm.StreamId, OrderFulfillmentSteps.AwaitPaymentTimeout, reason, ct);

        reason = await StartCancellationAsync(pm, reason, ct);
        await _bus.SendAsync(
            new CancelOrder(pm.OrderId, reason, Actor.Id),
            causing, Actor, IdempotencyKeys.ForProcessManager(pm.StreamId, OrderFulfillmentSteps.CancelOrder), ct);
        pm.CompleteAsCancelled();
        await _pms.SaveAsync(pm, ct);
    }

    // Cancellation intent is durable before side effects. Each successful release
    // joins a bounded batch; retries resume unreleased lines with the same keys.
    // Terminal state follows all effects, so an interrupted batch remains retryable.
    public async Task CompensateWithReleasesAsync(
        OrderFulfillmentProcessManager pm, string reason, EventMetadata causing, CancellationToken ct)
    {
        await _delayQueue.CancelAsync(pm.StreamId, OrderFulfillmentSteps.AwaitDispatchTimeout, reason, ct);
        reason = await StartCancellationAsync(pm, reason, ct);
        await _bus.SendAsync(
            new CancelOrder(pm.OrderId, reason, Actor.Id),
            causing, Actor, IdempotencyKeys.ForProcessManager(pm.StreamId, OrderFulfillmentSteps.CancelOrder), ct);

        await ReleaseReservationsAsync(pm, causing, ct);
        await _bus.SendAsync(
            new VoidPayment(pm.PaymentId, "Order fulfillment voided the authorized payment during compensation."),
            causing, Actor, IdempotencyKeys.ForProcessManager(pm.StreamId, OrderFulfillmentSteps.VoidPayment), ct);
        pm.RequestVoid(reason);
        pm.CompleteAsCancelled();
        await _pms.SaveAsync(pm, ct);
    }

    private async Task ReleaseReservationsAsync(
        OrderFulfillmentProcessManager pm, EventMetadata causing, CancellationToken ct)
    {
        var lines = pm.Reservations
            .Where(r => r.Value.Status == ReservationLineStatus.Reserved)
            .Select(r => (LineId: r.Key, InventoryId: r.Value.InventoryId!.Value))
            .ToList();
        foreach (var line in lines)
        {
            await _bus.SendAsync(
                new ReleaseInventory(line.InventoryId, line.LineId,
                    "Order fulfillment released the reservation during compensation."),
                causing, Actor, IdempotencyKeys.ForProcessManager(pm.StreamId, OrderFulfillmentSteps.Release, line.LineId), ct);
            pm.ReleaseReservation(line.LineId);
            await OrderFulfillmentPersistence.SaveBatchIfFullAsync(pm, _pms, ct);
        }
        await _pms.SaveAsync(pm, ct);
    }

    private async Task<string> StartCancellationAsync(
        OrderFulfillmentProcessManager pm, string reason, CancellationToken ct)
    {
        if (pm.CancellationReason is null)
        {
            pm.StartCancellation(reason);
            await _pms.SaveAsync(pm, ct);
        }
        return pm.CancellationReason!;
    }
}
