using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Projections.OrderDetail;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Handler behaviour for the eight Sales handlers plus the ShipmentScheduled mapping
// populator (commit 16's nine of sixteen subscriptions). Each handler applies its
// relational write and appends exactly one timeline entry; the timeline carries the
// CLR type name and envelope fields, and its payload round-trips back to the event.
public class OrderDetailProjectionTests
{
    private static readonly DateTime At = new(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SystemAt = new(2026, 5, 20, 9, 0, 5, DateTimeKind.Utc);

    [Fact]
    public async Task OrderDrafted_creates_header_as_draft_and_appends_timeline()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, customerId, At), position: 1), CancellationToken.None);

        var header = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        header.Status.Should().Be(OrderStatus.Draft);
        header.CustomerId.Should().Be(customerId);
        var timeline = await store.GetTimelineAsync(orderId, CancellationToken.None);
        timeline.Should().ContainSingle().Which.EventType.Should().Be(nameof(OrderDrafted));
    }

    [Fact]
    public async Task OrderLineAdded_inserts_line_and_appends_timeline()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 1), CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderLineAdded(orderId, lineId, "SKU-1", 2, new Money(5m, Currency.USD), At),
                position: 2),
            CancellationToken.None);

        var lines = await store.GetLinesAsync(orderId, CancellationToken.None);
        lines.Should().ContainSingle();
        lines[0].Sku.Should().Be("SKU-1");
        lines[0].Quantity.Should().Be(2);
        lines[0].UnitPrice.Should().Be(new Money(5m, Currency.USD));
        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(OrderLineAdded));
    }

    [Fact]
    public async Task OrderLineRemoved_deletes_line_and_appends_timeline()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderLineAdded(orderId, lineId, "SKU-1", 1, new Money(5m, Currency.USD), At),
                position: 2),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderLineRemoved(orderId, lineId, At), position: 3), CancellationToken.None);

        (await store.GetLinesAsync(orderId, CancellationToken.None)).Should().BeEmpty();
        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(OrderLineRemoved));
    }

    [Fact]
    public async Task ShippingAddressSet_writes_address_and_appends_timeline()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        var address = new Address("12 Main St", "Portland", "97201", "US");
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 1), CancellationToken.None);

        await projection.HandleAsync(
            Context(new ShippingAddressSet(orderId, address, At), position: 2), CancellationToken.None);

        (await store.GetHeaderAsync(orderId, CancellationToken.None))!.ShippingAddress
            .Should().Be(address);
        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(ShippingAddressSet));
    }

    [Fact]
    public async Task OrderPlaced_sets_status_total_placed_and_appends_timeline()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 1), CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(125m, Currency.USD), At),
                position: 2),
            CancellationToken.None);

        var header = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        header.Status.Should().Be(OrderStatus.Placed);
        header.PlacedUtc.Should().Be(At);
        header.Total.Should().Be(new Money(125m, Currency.USD));
        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(OrderPlaced));
    }

    [Fact]
    public async Task OrderShipped_sets_status_and_shipped_utc()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 1), CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderShipped(orderId, "UPS", "1Z999", At), position: 2), CancellationToken.None);

        var header = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        header.Status.Should().Be(OrderStatus.Shipped);
        header.ShippedUtc.Should().Be(At);
    }

    [Fact]
    public async Task OrderCancelled_sets_status_and_cancelled_utc()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 1), CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderCancelled(orderId, "out of stock", Guid.NewGuid(), At), position: 2),
            CancellationToken.None);

        var header = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        header.Status.Should().Be(OrderStatus.Cancelled);
        header.CancelledUtc.Should().Be(At);
    }

    [Fact]
    public async Task OrderCompleted_sets_status_and_completed_utc()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 1), CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderCompleted(orderId, At), position: 2), CancellationToken.None);

        var header = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        header.Status.Should().Be(OrderStatus.Completed);
        header.CompletedUtc.Should().Be(At);
    }

    [Fact]
    public async Task ShipmentScheduled_records_mapping_and_is_resolvable()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var shipmentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new ShipmentScheduled(
                shipmentId, orderId, new Address("12 Main St", "Portland", "97201", "US"), [], At),
                position: 1),
            CancellationToken.None);

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderIdByShipmentIdAsync(shipmentId, CancellationToken.None)).Should().Be(orderId);
        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(ShipmentScheduled));
    }

    [Fact]
    public async Task Timeline_entry_event_type_is_the_clr_type_name()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), At), position: 1),
            CancellationToken.None);

        // The CLR type name, equal to the event store's registered token by
        // construction (ADR 0018). No assembly-qualified or namespaced form.
        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Single().EventType.Should().Be("OrderPlaced");
    }

    [Fact]
    public async Task Timeline_carries_envelope_position_and_time_not_payload()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 7, occurredUtc: SystemAt),
            CancellationToken.None);

        var entry = (await store.GetTimelineAsync(orderId, CancellationToken.None)).Single();
        // GlobalPosition and OccurredUtc come from the envelope, not the payload's
        // DraftedUtc (At).
        entry.GlobalPosition.Should().Be(7);
        entry.OccurredUtc.Should().Be(SystemAt);
    }

    [Fact]
    public async Task Timeline_payload_deserializes_back_to_the_event()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        var placed = new OrderPlaced(orderId, Guid.NewGuid(), new Money(42.50m, Currency.USD), At);

        await projection.HandleAsync(Context(placed, position: 1), CancellationToken.None);

        var entry = (await store.GetTimelineAsync(orderId, CancellationToken.None)).Single();
        JsonSerializer.Deserialize<OrderPlaced>(entry.Payload, JsonOptions).Should().Be(placed);
    }

    [Fact]
    public async Task Handler_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        // The draft commits at position 10, so the checkpoint sits at 10.
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 10), CancellationToken.None);

        // OrderShipped redelivered at position 10 (at the checkpoint) returns early:
        // the status stays Draft and no second timeline entry is appended.
        await projection.HandleAsync(
            Context(new OrderShipped(orderId, "UPS", "1Z999", At), position: 10), CancellationToken.None);

        (await store.GetHeaderAsync(orderId, CancellationToken.None))!.Status.Should().Be(OrderStatus.Draft);
        (await store.GetTimelineAsync(orderId, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task Redelivery_does_not_append_a_duplicate_timeline_entry()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        var drafted = new OrderDrafted(orderId, Guid.NewGuid(), At);

        await projection.HandleAsync(Context(drafted, position: 5), CancellationToken.None);
        await projection.HandleAsync(Context(drafted, position: 5), CancellationToken.None);

        (await store.GetTimelineAsync(orderId, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task Line_remove_then_re_add_reflects_the_new_values()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderLineAdded(orderId, lineId, "SKU-1", 1, new Money(5m, Currency.USD), At),
                position: 2),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderLineRemoved(orderId, lineId, At), position: 3), CancellationToken.None);

        // The aggregate frees the LineId on removal, so a re-add of the same LineId
        // is a valid stream; the projection lands the new sku and quantity.
        await projection.HandleAsync(
            Context(new OrderLineAdded(orderId, lineId, "SKU-2", 9, new Money(7m, Currency.USD), At),
                position: 4),
            CancellationToken.None);

        var lines = await store.GetLinesAsync(orderId, CancellationToken.None);
        lines.Should().ContainSingle();
        lines[0].Sku.Should().Be("SKU-2");
        lines[0].Quantity.Should().Be(9);
    }

    [Fact]
    public async Task Lifecycle_produces_one_timeline_entry_per_event_ordered_by_position()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderLineAdded(orderId, Guid.NewGuid(), "SKU-1", 1, new Money(5m, Currency.USD), At),
                position: 2),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(5m, Currency.USD), At), position: 3),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderShipped(orderId, "UPS", "1Z999", At), position: 4), CancellationToken.None);

        var timeline = await store.GetTimelineAsync(orderId, CancellationToken.None);
        timeline.Select(t => t.EventType).Should().ContainInOrder(
            nameof(OrderDrafted), nameof(OrderLineAdded), nameof(OrderPlaced), nameof(OrderShipped));
        timeline.Select(t => t.GlobalPosition).Should().ContainInOrder(1L, 2L, 3L, 4L);
    }

    [Fact]
    public async Task Separate_orders_keep_separate_timelines()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var order1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderDrafted(order1, Guid.NewGuid(), At), position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderDrafted(order2, Guid.NewGuid(), At), position: 2), CancellationToken.None);

        (await store.GetTimelineAsync(order1, CancellationToken.None)).Should().ContainSingle();
        (await store.GetTimelineAsync(order2, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task OrderLineAdded_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At), position: 10), CancellationToken.None);

        // A redelivered OrderLineAdded at position 10 returns early, so no line lands.
        await projection.HandleAsync(
            Context(new OrderLineAdded(orderId, Guid.NewGuid(), "SKU-1", 1, new Money(5m, Currency.USD), At),
                position: 10),
            CancellationToken.None);

        (await store.GetLinesAsync(orderId, CancellationToken.None)).Should().BeEmpty();
    }

    private static OrderDetailProjection Projection(InMemoryOrderDetailStore store)
        => new(store, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

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
