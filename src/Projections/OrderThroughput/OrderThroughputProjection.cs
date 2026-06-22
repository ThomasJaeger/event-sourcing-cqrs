using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;

namespace EventSourcingCqrs.Projections.OrderThroughput;

// Pattern from Chapter 13: the order-throughput meter. Counts the eight Sales
// order events into per-second buckets keyed on each event's occurrence-second
// (EventMetadata.OccurredUtc), so a replayed stream buckets by when each event
// happened, not by wall-clock now. Shape A discards the event type: every order
// event counts the same.
//
// Each handler runs one flow: open the unit of work, skip a redelivery on the
// checkpoint, count the event into its occurrence-second bucket, then commit the
// increment and the checkpoint together. The eight handlers differ only by event
// type; Shape A buckets them identically.
public sealed class OrderThroughputProjection
    : IProjection,
      IEventHandler<OrderDrafted>,
      IEventHandler<OrderLineAdded>,
      IEventHandler<OrderLineRemoved>,
      IEventHandler<ShippingAddressSet>,
      IEventHandler<OrderPlaced>,
      IEventHandler<OrderShipped>,
      IEventHandler<OrderCancelled>,
      IEventHandler<OrderCompleted>
{
    // The bounded-state retention window: the meter keeps only buckets within this
    // span of an event's occurrence-second, pruning older buckets on each write.
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromSeconds(300);

    public string Name => ProjectionNames.OrderThroughput;

    private readonly IOrderThroughputStore _store;

    public OrderThroughputProjection(IOrderThroughputStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public Task HandleAsync(EventContext<OrderDrafted> context, CancellationToken ct) => HandleOrderEventAsync(context, ct);
    public Task HandleAsync(EventContext<OrderLineAdded> context, CancellationToken ct) => HandleOrderEventAsync(context, ct);
    public Task HandleAsync(EventContext<OrderLineRemoved> context, CancellationToken ct) => HandleOrderEventAsync(context, ct);
    public Task HandleAsync(EventContext<ShippingAddressSet> context, CancellationToken ct) => HandleOrderEventAsync(context, ct);
    public Task HandleAsync(EventContext<OrderPlaced> context, CancellationToken ct) => HandleOrderEventAsync(context, ct);
    public Task HandleAsync(EventContext<OrderShipped> context, CancellationToken ct) => HandleOrderEventAsync(context, ct);
    public Task HandleAsync(EventContext<OrderCancelled> context, CancellationToken ct) => HandleOrderEventAsync(context, ct);
    public Task HandleAsync(EventContext<OrderCompleted> context, CancellationToken ct) => HandleOrderEventAsync(context, ct);

    // The one flow the eight handlers share: skip a redelivery, count the event
    // into its occurrence-second bucket, prune buckets outside the retention
    // window, stage a notification, and commit the count, prune, and notification
    // together with the checkpoint.
    private async Task HandleOrderEventAsync<TEvent>(EventContext<TEvent> context, CancellationToken ct)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        // Count this event into the bucket for the second it occurred, so a replay
        // buckets by when the event happened, not by wall-clock now.
        var second = SecondOf(context.Metadata.OccurredUtc);
        await uow.IncrementSecondAsync(second, ct);
        // Bound the per-tenant state: prune every bucket older than the window,
        // measured from this event's second, on the same commit as the count.
        await uow.PruneBeforeAsync(second - RetentionWindow, ct);
        // Stage one collection-scoped notification under the tenant-wide metrics
        // sentinel, so a subscribed dashboard re-queries on any order event. The
        // concrete event name rides as the informational field; the subscriber
        // coalesces on the tenant and resource, not on the event name.
        uow.PublishOnCommit(new NotificationEnvelope(
            Name, CollectionResourceIds.OrderThroughputMetrics, typeof(TEvent).Name, [], context.Metadata.Tenant));
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    // Floors an occurrence instant to its whole UTC second, the bucket key. Private
    // to the projection: no shared time-bucketing utility until a second consumer
    // needs one.
    private static DateTime SecondOf(DateTime occurredUtc)
        => occurredUtc.AddTicks(-(occurredUtc.Ticks % TimeSpan.TicksPerSecond));
}
