using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Projections.CustomerSummary;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class CustomerSummaryProjectionTests
{
    private static readonly DateTime PlacedAt = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LaterAt = new(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SystemAt = new(2026, 5, 14, 9, 0, 5, DateTimeKind.Utc);

    [Fact]
    public async Task OrderPlaced_creates_a_summary_with_count_one_and_lifetime_equal_to_total()
    {
        var store = new InMemoryCustomerSummaryStore();
        var projection = Projection(store);
        var customerId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), customerId, new Money(50m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: SystemAt),
            CancellationToken.None);

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(1);
        row.LifetimeValue.Should().Be(new Money(50m, Currency.USD));
        row.LastOrderUtc.Should().Be(PlacedAt);
        row.LastUpdatedUtc.Should().Be(SystemAt);
    }

    [Fact]
    public async Task OrderPlaced_records_the_per_order_lookup_row()
    {
        var store = new InMemoryCustomerSummaryStore();
        var projection = Projection(store);
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, customerId, new Money(50m, Currency.USD), PlacedAt),
                position: 1),
            CancellationToken.None);

        // The lookup row is what a later OrderCancelled resolves against.
        await using var uow = await store.BeginAsync(CancellationToken.None);
        (await uow.GetOrderByOrderIdAsync(orderId, CancellationToken.None))
            .Should().Be(new CustomerSummaryOrderRow(
                customerId, orderId, new Money(50m, Currency.USD), PlacedAt));
    }

    [Fact]
    public async Task Two_placements_for_the_same_customer_accumulate()
    {
        var store = new InMemoryCustomerSummaryStore();
        var projection = Projection(store);
        var customerId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), customerId, new Money(50m, Currency.USD), PlacedAt),
                position: 1),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), customerId, new Money(30m, Currency.USD), LaterAt),
                position: 2),
            CancellationToken.None);

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(2);
        row.LifetimeValue.Should().Be(new Money(80m, Currency.USD));
        row.LastOrderUtc.Should().Be(LaterAt);
    }

    [Fact]
    public async Task OrderCancelled_nets_the_placement_back_out_and_deletes_the_lookup_row()
    {
        var store = new InMemoryCustomerSummaryStore();
        var projection = Projection(store);
        var customerId = Guid.NewGuid();
        var cancelledOrder = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderPlaced(cancelledOrder, customerId, new Money(50m, Currency.USD), PlacedAt),
                position: 1),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), customerId, new Money(30m, Currency.USD), LaterAt),
                position: 2),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderCancelled(cancelledOrder, "out of stock", Guid.NewGuid(), LaterAt),
                position: 3, occurredUtc: LaterAt),
            CancellationToken.None);

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(1);
        row.LifetimeValue.Should().Be(new Money(30m, Currency.USD));
        // last_order_utc is not recomputed on cancellation (ADR 0019).
        row.LastOrderUtc.Should().Be(LaterAt);
        // The per-order lookup row is deleted once the cancellation nets out.
        await using var uow = await store.BeginAsync(CancellationToken.None);
        (await uow.GetOrderByOrderIdAsync(cancelledOrder, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Place_then_cancel_the_only_order_leaves_the_row_with_count_zero()
    {
        var store = new InMemoryCustomerSummaryStore();
        var projection = Projection(store);
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, customerId, new Money(50m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: SystemAt),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderCancelled(orderId, "out of stock", Guid.NewGuid(), LaterAt),
                position: 2, occurredUtc: LaterAt),
            CancellationToken.None);

        // The summary row persists with count 0 and zeroed lifetime; the v1
        // staleness keeps last_order_utc at the original placement (ADR 0019).
        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(0);
        row.LifetimeValue.Should().Be(new Money(0m, Currency.USD));
        row.LastOrderUtc.Should().Be(PlacedAt);
        row.LastUpdatedUtc.Should().Be(LaterAt);
    }

    [Fact]
    public async Task OrderCancelled_with_no_lookup_row_no_ops_and_advances_the_checkpoint()
    {
        var store = new InMemoryCustomerSummaryStore();
        var projection = Projection(store);

        // No prior OrderPlaced, so no lookup row. The cancel nets nothing and
        // creates no summary, but the checkpoint still advances.
        await projection.HandleAsync(
            Context(new OrderCancelled(Guid.NewGuid(), "changed mind", Guid.NewGuid(), PlacedAt),
                position: 7),
            CancellationToken.None);

        store.Checkpoints[projection.Name].Should().Be(7);
    }

    [Fact]
    public async Task A_placement_after_a_cancellation_accumulates_from_the_netted_state()
    {
        var store = new InMemoryCustomerSummaryStore();
        var projection = Projection(store);
        var customerId = Guid.NewGuid();
        var firstOrder = Guid.NewGuid();

        // Place two (count 2, lifetime 80), cancel the first (netted to count 1,
        // lifetime 30), then place a third.
        await projection.HandleAsync(
            Context(new OrderPlaced(firstOrder, customerId, new Money(50m, Currency.USD), PlacedAt),
                position: 1),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), customerId, new Money(30m, Currency.USD), LaterAt),
                position: 2),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderCancelled(firstOrder, "out of stock", Guid.NewGuid(), LaterAt),
                position: 3),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), customerId, new Money(20m, Currency.USD), LaterAt),
                position: 4),
            CancellationToken.None);

        // The third placement builds on the netted state, not a corrupted one:
        // count 1 + 1 = 2, lifetime 30 + 20 = 50.
        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(2);
        row.LifetimeValue.Should().Be(new Money(50m, Currency.USD));
    }

    [Fact]
    public async Task OrderPlaced_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryCustomerSummaryStore();
        var projection = Projection(store);
        var customerId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), customerId, new Money(50m, Currency.USD), PlacedAt),
                position: 10),
            CancellationToken.None);

        // A redelivery at position 10 (at the checkpoint) returns early, so the
        // second placement does not accumulate.
        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), customerId, new Money(99m, Currency.USD), PlacedAt),
                position: 10),
            CancellationToken.None);

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(1);
        row.LifetimeValue.Should().Be(new Money(50m, Currency.USD));
    }

    [Fact]
    public async Task OrderCancelled_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryCustomerSummaryStore();
        var projection = Projection(store);
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // Place at 5, then a later placement advances the checkpoint to 20.
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, customerId, new Money(50m, Currency.USD), PlacedAt),
                position: 5),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), customerId, new Money(30m, Currency.USD), LaterAt),
                position: 20),
            CancellationToken.None);

        // A stale OrderCancelled at position 15 redelivers after the checkpoint
        // moved to 20: the handler returns early, so no netting happens.
        await projection.HandleAsync(
            Context(new OrderCancelled(orderId, "out of stock", Guid.NewGuid(), LaterAt),
                position: 15),
            CancellationToken.None);

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(2);
        store.Checkpoints[projection.Name].Should().Be(20);
    }

    [Fact]
    public async Task OrderPlaced_stages_no_notification_because_customer_summary_has_no_v1_subscriber()
    {
        var store = new InMemoryCustomerSummaryStore();
        var projection = Projection(store);

        await projection.HandleAsync(
            Context(new OrderPlaced(Guid.NewGuid(), Guid.NewGuid(), new Money(50m, Currency.USD), PlacedAt),
                position: 1),
            CancellationToken.None);

        // CustomerSummary carries the publish mechanism so the unit-of-work contract
        // stays uniform, but no page subscribes to it in v1, so its handler stages
        // nothing (born-at-consumer).
        store.StagedNotifications.Should().BeEmpty();
    }

    private static CustomerSummaryProjection Projection(InMemoryCustomerSummaryStore store)
        => new(store, NullLogger<CustomerSummaryProjection>.Instance);

    private static EventContext<TEvent> Context<TEvent>(
        TEvent @event, long position, DateTime? occurredUtc = null)
        where TEvent : IDomainEvent
        => new(@event, Metadata(occurredUtc ?? SystemAt), position);

    private static EventMetadata Metadata(DateTime occurredUtc)
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: occurredUtc);
}
