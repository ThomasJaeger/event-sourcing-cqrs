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
        StageNotification(uow, nameof(InventoryCreated), context.Metadata.Tenant);
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
            StageNotification(uow, nameof(InventoryAdjusted), context.Metadata.Tenant);
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
            StageNotification(uow, nameof(InventoryReserved), context.Metadata.Tenant);
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
                StageNotification(uow, nameof(InventoryReleased), context.Metadata.Tenant);
            }
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // Stages a collection-scoped inventory notification for in-process dispatch
    // to the subscribed dashboard circuits (ADR 0032). Keyed by the AllInventory
    // sentinel rather than a per-sku id, since the InventoryDashboard is a
    // collection page that re-queries the whole list on any change (ADR 0033).
    // Carries no row data: the page reads authoritative state on receipt. The
    // adjust, reserve, and release handlers stage only when their mutation
    // returned a row, so a zero-row change stages nothing.
    private void StageNotification(IInventoryDashboardUnitOfWork uow, string eventName, TenantId tenant)
        => uow.PublishOnCommit(new NotificationEnvelope(Name, CollectionResourceIds.AllInventory, eventName, [], tenant));
}
