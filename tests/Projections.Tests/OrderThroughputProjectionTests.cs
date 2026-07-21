using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Projections.OrderThroughput;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The order-throughput projection buckets the Sales order events by their
// occurrence-second (EventMetadata.OccurredUtc): same-second events of any type
// aggregate into one bucket, and a past event buckets by its own second rather
// than wall-clock now. The structural guard pins the meter to exactly the eight
// Sales events, so widening or narrowing the tracked set trips the build.
public class OrderThroughputProjectionTests
{
    private static readonly DateTime SecondNow = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondPast = new(2026, 5, 14, 8, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Order_events_bucket_by_occurrence_second_aggregating_same_second_and_keying_past_on_its_own_second()
    {
        var store = new InMemoryOrderThroughputStore();
        var projection = new OrderThroughputProjection(store);

        // Three distinct Sales event types whose OccurredUtc fall in the SAME
        // second (sub-second apart). Shape A discards the type and buckets on the
        // second, so they must collapse into one bucket.
        await projection.HandleAsync(
            Context(new OrderDrafted(Guid.NewGuid(), Guid.NewGuid(), SecondNow, "web"),
                position: 1, occurredUtc: SecondNow),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderShipped(Guid.NewGuid(), "DHL", "TRK-1", SecondNow),
                position: 2, occurredUtc: SecondNow.AddMilliseconds(250)),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderCompleted(Guid.NewGuid(), SecondNow),
                position: 3, occurredUtc: SecondNow.AddMilliseconds(900)),
            CancellationToken.None);

        // A past-second event: its bucket must key on its own OccurredUtc second,
        // not on wall-clock now.
        await projection.HandleAsync(
            Context(new OrderCancelled(Guid.NewGuid(), "changed mind", Guid.NewGuid(), SecondPast),
                position: 4, occurredUtc: SecondPast.AddMilliseconds(100)),
            CancellationToken.None);

        var buckets = await store.GetBucketsAsync(CancellationToken.None);

        // (a) Same-second aggregation: the three SecondNow events collapse into a
        // single bucket with count 3.
        buckets.Where(b => b.SecondUtc == SecondNow).Should().ContainSingle()
            .Which.Count.Should().Be(3);

        // (b) Replay-correctness: the past event buckets at its own occurrence
        // second, not at "now".
        buckets.Where(b => b.SecondUtc == SecondPast).Should().ContainSingle()
            .Which.Count.Should().Be(1);
    }

    // Green-on-write structural guard: the meter tracks EXACTLY the eight Sales
    // events. Reflecting the closed IEventHandler<TEvent> set the dispatcher would
    // forward (the same enumeration AddProjection uses) pins the tracked-set
    // boundary the runtime cannot assert behaviorally: adding a ninth handler
    // (widening the meter) or dropping one trips this test.
    [Fact]
    public void OrderThroughputProjection_handles_exactly_the_eight_sales_events()
    {
        var handled = typeof(OrderThroughputProjection).GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>))
            .Select(i => i.GetGenericArguments()[0])
            .ToList();

        var expected = new[]
        {
            typeof(OrderDrafted),
            typeof(OrderLineAdded),
            typeof(OrderLineRemoved),
            typeof(ShippingAddressSet),
            typeof(OrderPlaced),
            typeof(OrderShipped),
            typeof(OrderCancelled),
            typeof(OrderCompleted),
        };

        handled.Should().HaveCount(expected.Length);
        handled.Should().Contain(expected);
    }

    // Retention (Option A): on write the meter prunes buckets older than a
    // 300-second window, so a tenant's bucket count stays bounded however long the
    // stream runs. The window is pinned here in the test; GREEN introduces the
    // projection-side constant the production prune uses.
    [Fact]
    public async Task Retention_prunes_buckets_older_than_the_300_second_window_to_a_bounded_set()
    {
        const int WindowSeconds = 300;
        var store = new InMemoryOrderThroughputStore();
        var projection = new OrderThroughputProjection(store);

        // Eight Sales events for one tenant spanning MORE than the window: the first
        // is 400 seconds before the latest (out of a 300-second window), the next six
        // fall within it, and the last sits at the latest occurrence-second. Payloads
        // are irrelevant to the meter; only OccurredUtc keys the bucket.
        var latest = new DateTime(2026, 5, 14, 10, 0, 0, DateTimeKind.Utc);
        DateTime At(int secondsBefore) => latest.AddSeconds(-secondsBefore);

        await projection.HandleAsync(
            Context(new OrderDrafted(Guid.NewGuid(), Guid.NewGuid(), At(400), "web"), position: 1, occurredUtc: At(400)),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderLineAdded(Guid.NewGuid(), Guid.NewGuid(), "SKU-1", 1, new Money(10m, Currency.USD), At(250)),
                position: 2, occurredUtc: At(250)),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderLineRemoved(Guid.NewGuid(), Guid.NewGuid(), At(200)), position: 3, occurredUtc: At(200)),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new ShippingAddressSet(Guid.NewGuid(), new Address("1 Main", "Town", "00000", "US"), At(150)),
                position: 4, occurredUtc: At(150)),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), Guid.NewGuid(), new Money(99m, Currency.USD), At(100)),
                position: 5, occurredUtc: At(100)),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderShipped(Guid.NewGuid(), "DHL", "TRK-1", At(60)), position: 6, occurredUtc: At(60)),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderCancelled(Guid.NewGuid(), "changed mind", Guid.NewGuid(), At(30)),
                position: 7, occurredUtc: At(30)),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderCompleted(Guid.NewGuid(), latest), position: 8, occurredUtc: latest),
            CancellationToken.None);

        var buckets = await store.GetBucketsAsync(CancellationToken.None);
        var cutoff = latest.AddSeconds(-WindowSeconds);
        var inWindowSeconds = new[] { At(250), At(200), At(150), At(100), At(60), At(30), latest };

        // (a) Every bucket strictly older than the window's cutoff is pruned: the
        // 400-seconds-before event must be gone.
        buckets.Should().NotContain(b => b.SecondUtc < cutoff,
            "buckets older than the 300-second window must be pruned on write");

        // (b) Every in-window occurrence-second survives with its count of one.
        foreach (var second in inWindowSeconds)
        {
            buckets.Where(b => b.SecondUtc == second).Should().ContainSingle()
                .Which.Count.Should().Be(1);
        }

        // (c) The per-tenant bucket count is bounded by the window and equals the
        // number of distinct in-window occurrence-seconds written.
        buckets.Should().HaveCount(inWindowSeconds.Length);
        buckets.Count.Should().BeLessThanOrEqualTo(WindowSeconds);
    }

    // Projection-side staging: each handled order event stages one collection-scoped
    // notification whose coalescing-key fields are identity-equal across a burst.
    //
    // Subscriber-side collapse-to-one-re-query is the ResourceNotificationDispatcher's
    // own proven behavior: it keys delivery and coalescing on ResourceKey = Tenant +
    // ResourceType + ResourceId, with EventName NOT participating, so a burst sharing
    // those fields collapses to one page re-query. That is not re-asserted here.
    // ResourceKey is internal to the Web host, so this test asserts on the public
    // NotificationEnvelope fields that map into the key (ProjectionName, ResourceId,
    // Tenant); it proves only that the projection stages envelopes whose coalescing-key
    // fields are identity-equal.
    [Fact]
    public async Task Each_handled_event_stages_a_notification_whose_coalescing_key_fields_are_identity_equal()
    {
        var store = new InMemoryOrderThroughputStore();
        var projection = new OrderThroughputProjection(store);
        var second = new DateTime(2026, 5, 14, 11, 0, 0, DateTimeKind.Utc);
        var tenant = TenantId.From(Guid.Parse("77777777-7777-7777-7777-777777777777"));

        // A same-second, same-tenant burst of three different Sales event types.
        await projection.HandleAsync(
            Context(new OrderDrafted(Guid.NewGuid(), Guid.NewGuid(), second, "web"),
                position: 1, occurredUtc: second, tenant: tenant),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderShipped(Guid.NewGuid(), "DHL", "TRK-1", second),
                position: 2, occurredUtc: second.AddMilliseconds(300), tenant: tenant),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderCompleted(Guid.NewGuid(), second),
                position: 3, occurredUtc: second.AddMilliseconds(800), tenant: tenant),
            CancellationToken.None);

        var staged = store.StagedNotifications;

        // One notification staged per handled event.
        staged.Should().HaveCount(3);

        // Each carries the projection name, the metrics sentinel, the events' tenant,
        // and no widgets.
        staged.Should().OnlyContain(e =>
            e.ProjectionName == projection.Name
            && e.ResourceId == CollectionResourceIds.OrderThroughputMetrics
            && e.Tenant == tenant
            && e.Widgets.Count == 0);

        // The coalescing-key fields (ProjectionName, ResourceId, Tenant) are
        // identity-equal across the burst, even though EventName may differ per event.
        staged.Select(e => (e.ProjectionName, e.ResourceId, e.Tenant)).Distinct().Should().ContainSingle();
    }

    private static EventContext<TEvent> Context<TEvent>(
        TEvent @event, long position, DateTime occurredUtc, TenantId? tenant = null)
        where TEvent : IDomainEvent
        => new(@event, Metadata(occurredUtc, tenant), position);

    private static EventMetadata Metadata(DateTime occurredUtc, TenantId? tenant = null)
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: occurredUtc,
            Tenant: tenant ?? WellKnownTenants.Default);
}
