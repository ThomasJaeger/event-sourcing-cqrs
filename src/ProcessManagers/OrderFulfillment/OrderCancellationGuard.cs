using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// Called under the same order lock as dispatch and orchestration. Use the
// authoritative shipment, not the possibly delayed ShipmentDispatched event.
public sealed class OrderCancellationGuard(
    IProcessManagerRepository<OrderFulfillmentProcessManager> pms,
    IEventStoreRepository<Shipment> shipments,
    ICurrentTenantAccessor tenants) : IOrderCancellationGuard
{
    public async Task EnsureCanCancelAsync(Guid orderId, CancellationToken ct)
    {
        var pm = await pms.LoadAsync(OrderFulfillmentStreams.For(
            tenants.Current ?? throw new MissingTenantContextException(), orderId), OrderFulfillmentStreams.New, ct);
        if (pm is null || pm.ShipmentId == Guid.Empty) return;
        var shipment = await shipments.LoadAsync(pm.ShipmentId, ct);
        if (shipment is { Status: not ShipmentStatus.Scheduled })
            throw new DomainException("Cannot cancel an order whose shipment has already dispatched.");
    }
}
