using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using Microsoft.Extensions.Logging;

namespace EventSourcingCqrs.Projections.CustomerSummary;

// Pattern from Chapter 13: per-customer summary stats (order count, lifetime
// value, last-order date). Subscribes to OrderPlaced and OrderCancelled and
// nets cancellations out of the per-customer aggregate (ADR 0019).
//
// OrderCancelled carries only OrderId, so the projection cannot net a
// cancellation from the event alone. It records a projection-private per-order
// lookup row on placement and recovers the customer and total from it on
// cancellation. Each handler reads the checkpoint, applies its writes, and
// commits in one transaction; the skip-guard makes redelivery a no-op.
public sealed class CustomerSummaryProjection
    : IProjection,
      IEventHandler<OrderPlaced>,
      IEventHandler<OrderCancelled>
{
    public string Name => ProjectionNames.CustomerSummary;

    private readonly ICustomerSummaryStore _store;
    private readonly ILogger<CustomerSummaryProjection> _logger;

    public CustomerSummaryProjection(
        ICustomerSummaryStore store, ILogger<CustomerSummaryProjection> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
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
        // Accumulate the summary and record the per-order lookup row so a later
        // OrderCancelled (which carries neither customer nor total) can recover
        // them. Both writes and the checkpoint advance commit as one unit.
        await uow.ApplyPlacementAsync(
            e.CustomerId, e.Total, e.PlacedUtc, context.Metadata.OccurredUtc, ct);
        await uow.InsertOrderAsync(
            new CustomerSummaryOrderRow(e.CustomerId, e.OrderId, e.Total, e.PlacedUtc), ct);
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
        // OrderCancelled carries only OrderId. Recover the customer and total
        // from the per-order lookup, net them out, and delete the lookup row. A
        // missing row (a cancel of an order placed before this projection was
        // deployed, or a rebuild edge) no-ops with a debug log; the checkpoint
        // still advances so the event is not reprocessed.
        var order = await uow.GetOrderByOrderIdAsync(context.Event.OrderId, ct);
        if (order is null)
        {
            _logger.LogDebug(
                "OrderCancelled for order {OrderId} has no customer_summary_orders " +
                "lookup row; no netting applied.",
                context.Event.OrderId);
        }
        else
        {
            await uow.ApplyCancellationAsync(
                order.CustomerId, order.Total, context.Metadata.OccurredUtc, ct);
            await uow.DeleteOrderAsync(order.CustomerId, order.OrderId, ct);
        }
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }
}
