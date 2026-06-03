using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using Microsoft.Extensions.Logging;

namespace EventSourcingCqrs.Projections.InventoryDashboard;

// Pattern from Chapter 13: per-SKU inventory dashboard. Subscribes to the four
// Fulfillment inventory events and derives on-hand quantity (a running sum of
// adjustments) and reserved quantity (reserves minus releases).
//
// InventoryReleased carries no quantity (the lean-compensating-event shape,
// ADR 0020), so the projection records a per-reservation lookup row on
// InventoryReserved and recovers the quantity from it on release. Each handler
// reads the checkpoint, applies its writes, and commits in one transaction; the
// skip-guard makes redelivery a no-op.
public sealed class InventoryDashboardProjection
    : IProjection,
      IEventHandler<InventoryCreated>,
      IEventHandler<InventoryAdjusted>,
      IEventHandler<InventoryReserved>,
      IEventHandler<InventoryReleased>
{
    public string Name => "inventory-dashboard";

    private readonly IInventoryDashboardStore _store;
    private readonly ILogger<InventoryDashboardProjection> _logger;

    public InventoryDashboardProjection(
        IInventoryDashboardStore store, ILogger<InventoryDashboardProjection> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    public async Task HandleAsync(EventContext<InventoryCreated> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        await uow.CreateDashboardAsync(
            context.Event.InventoryId, context.Event.Sku, context.Metadata.OccurredUtc, ct);
        StageNotification(uow, context.Event.Sku, nameof(InventoryCreated), context.Metadata.Tenant);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<InventoryAdjusted> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        // QuantityDelta is signed; the dashboard's on-hand is its running sum.
        var sku = await uow.AdjustOnHandAsync(
            context.Event.InventoryId, context.Event.QuantityDelta, context.Metadata.OccurredUtc, ct);
        if (sku is not null)
        {
            StageNotification(uow, sku, nameof(InventoryAdjusted), context.Metadata.Tenant);
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<InventoryReserved> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        var e = context.Event;
        // Add to reserved and record the per-reservation lookup row so a later
        // InventoryReleased (which carries no quantity) can recover it.
        var sku = await uow.AdjustReservedAsync(e.InventoryId, e.Quantity, context.Metadata.OccurredUtc, ct);
        await uow.InsertReservationAsync(
            new InventoryReservationRow(e.InventoryId, e.OrderId, e.LineId, e.Quantity, e.ReservedUtc), ct);
        if (sku is not null)
        {
            StageNotification(uow, sku, nameof(InventoryReserved), context.Metadata.Tenant);
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<InventoryReleased> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        // InventoryReleased carries no quantity (ADR 0020). Recover it from the
        // per-reservation lookup, subtract it from reserved, and delete the
        // lookup row. A missing row (a release of a reservation made before this
        // projection was deployed, or a rebuild edge) no-ops with a debug log;
        // the checkpoint still advances so the event is not reprocessed.
        var e = context.Event;
        var reservation = await uow.GetReservationAsync(e.InventoryId, e.OrderId, e.LineId, ct);
        if (reservation is null)
        {
            _logger.LogDebug(
                "InventoryReleased for inventory {InventoryId}, order {OrderId}, line {LineId} " +
                "has no inventory_reservations lookup row; no reserved adjustment applied.",
                e.InventoryId, e.OrderId, e.LineId);
        }
        else
        {
            var sku = await uow.AdjustReservedAsync(
                e.InventoryId, -reservation.Quantity, context.Metadata.OccurredUtc, ct);
            await uow.DeleteReservationAsync(e.InventoryId, e.OrderId, e.LineId, ct);
            if (sku is not null)
            {
                StageNotification(uow, sku, nameof(InventoryReleased), context.Metadata.Tenant);
            }
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // Stages an inventory notification for the SignalR hub, keyed by sku for the
    // inventory:{sku} group (D2). InventoryDashboard is a v1 subscriber. The sku
    // comes from the mutation's RETURNING clause, so a zero-row UPDATE stages
    // nothing. The widget set is empty for now: the page re-queries authoritative
    // state on any notification (D1), and the precise Chapter 13 widget vocabulary
    // lands with the Cluster 2 retrofit page.
    private void StageNotification(IInventoryDashboardUnitOfWork uow, string sku, string eventName, TenantId tenant)
        => uow.PublishOnCommit(new NotificationEnvelope(Name, sku, eventName, [], tenant));
}
