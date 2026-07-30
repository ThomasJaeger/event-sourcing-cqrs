using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Projections.OrderList;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class OrderListProjectionTests
{
    private static readonly DateTime PlacedAt = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ShippedAt = new(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime SystemAt = new(2026, 5, 14, 9, 0, 5, DateTimeKind.Utc);

    [Fact]
    public async Task OrderPlaced_handler_inserts_a_row_with_the_event_fields()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var placed = new OrderPlaced(orderId, customerId, new Money(149.95m, Currency.USD), PlacedAt);

        await projection.HandleAsync(Context(placed, position: 1), CancellationToken.None);

        var row = await store.GetAsync(orderId, CancellationToken.None);
        row.Should().NotBeNull();
        row!.OrderId.Should().Be(orderId);
        row.CustomerId.Should().Be(customerId);
        row.Status.Should().Be(OrderStatus.Placed);
        row.Total.Should().Be(new Money(149.95m, Currency.USD));
    }

    [Fact]
    public async Task OrderPlaced_handler_sources_placed_utc_from_the_event_and_last_updated_from_metadata()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        var placed = new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt);

        await projection.HandleAsync(
            Context(placed, position: 1, occurredUtc: SystemAt), CancellationToken.None);

        var row = await store.GetAsync(orderId, CancellationToken.None);
        // Business time is when the user placed the order; system time is when
        // the projection touched the row. They are distinct sources.
        row!.PlacedUtc.Should().Be(PlacedAt);
        row.LastUpdatedUtc.Should().Be(SystemAt);
    }

    [Fact]
    public async Task OrderShipped_handler_updates_status_and_last_updated_utc()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: PlacedAt),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderShipped(orderId, "UPS", "1Z999", ShippedAt),
                position: 2, occurredUtc: ShippedAt),
            CancellationToken.None);

        var row = await store.GetAsync(orderId, CancellationToken.None);
        row!.Status.Should().Be(OrderStatus.Shipped);
        row.LastUpdatedUtc.Should().Be(ShippedAt);
        // placed_utc is the placement time and does not move when the order ships.
        row.PlacedUtc.Should().Be(PlacedAt);
    }

    [Fact]
    public async Task OrderCancelled_handler_updates_status_and_last_updated_utc()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: PlacedAt),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderCancelled(orderId, "out of stock", Guid.NewGuid(), ShippedAt),
                position: 2, occurredUtc: ShippedAt),
            CancellationToken.None);

        var row = await store.GetAsync(orderId, CancellationToken.None);
        row!.Status.Should().Be(OrderStatus.Cancelled);
        row.LastUpdatedUtc.Should().Be(ShippedAt);
    }

    [Fact]
    public async Task OrderCancelled_handler_does_nothing_when_the_order_was_never_placed()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();

        // The order was cancelled while still a draft: OrderPlaced never fired,
        // so there is no row to update and none should be created.
        await projection.HandleAsync(
            Context(new OrderCancelled(orderId, "changed mind", Guid.NewGuid(), ShippedAt),
                position: 1),
            CancellationToken.None);

        (await store.GetAsync(orderId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task OrderPlaced_handler_is_idempotent_on_redelivery()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        var placed = new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt);

        await projection.HandleAsync(Context(placed, position: 1), CancellationToken.None);
        await projection.HandleAsync(Context(placed, position: 1), CancellationToken.None);

        var row = await store.GetAsync(orderId, CancellationToken.None);
        row.Should().NotBeNull();
        row!.Status.Should().Be(OrderStatus.Placed);
    }

    [Fact]
    public async Task OrderPlaced_redelivered_after_a_status_change_does_not_clobber_the_status()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        var placed = new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt);
        await projection.HandleAsync(Context(placed, position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderShipped(orderId, "UPS", "1Z999", ShippedAt), position: 2),
            CancellationToken.None);

        // A crash-recovery redelivery of OrderPlaced must not reset the row
        // to Placed: ON CONFLICT DO NOTHING keeps the shipped row.
        await projection.HandleAsync(Context(placed, position: 1), CancellationToken.None);

        var row = await store.GetAsync(orderId, CancellationToken.None);
        row!.Status.Should().Be(OrderStatus.Shipped);
    }

    [Fact]
    public async Task OrderPlaced_handler_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var firstOrderId = Guid.NewGuid();
        var secondOrderId = Guid.NewGuid();
        // First placement advances the checkpoint to position 10.
        await projection.HandleAsync(
            Context(new OrderPlaced(
                    firstOrderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt),
                position: 10),
            CancellationToken.None);
        var insertsAfterFirst = store.InsertCount;

        // A second OrderPlaced for a different order at position 10 (or below)
        // is an at-least-once redelivery from the projection's perspective:
        // the handler returns early without opening a row.
        await projection.HandleAsync(
            Context(new OrderPlaced(
                    secondOrderId, Guid.NewGuid(), new Money(20m, Currency.USD), PlacedAt),
                position: 10),
            CancellationToken.None);

        store.InsertCount.Should().Be(insertsAfterFirst);
        (await store.GetAsync(secondOrderId, CancellationToken.None)).Should().BeNull();
        store.Checkpoints[projection.Name].Should().Be(10);
    }

    [Fact]
    public async Task OrderShipped_handler_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        // Place the order at position 5, then cancel it at position 20: the
        // checkpoint moves to 20 and the row is Cancelled.
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt),
                position: 5),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderCancelled(orderId, "out of stock", Guid.NewGuid(), ShippedAt),
                position: 20, occurredUtc: ShippedAt),
            CancellationToken.None);
        var updatesAfterCancel = store.UpdateCount;

        // A stale OrderShipped from before the cancel arrives via redelivery at
        // position 10. Without the position-check the handler would clobber
        // Cancelled back to Shipped; the checkpoint comparison is what catches
        // this reordering.
        await projection.HandleAsync(
            Context(new OrderShipped(orderId, "UPS", "1Z999", ShippedAt),
                position: 10, occurredUtc: ShippedAt),
            CancellationToken.None);

        store.UpdateCount.Should().Be(updatesAfterCancel);
        (await store.GetAsync(orderId, CancellationToken.None))!
            .Status.Should().Be(OrderStatus.Cancelled);
        store.Checkpoints[projection.Name].Should().Be(20);
    }

    [Fact]
    public async Task OrderCancelled_handler_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        // Place, then ship at position 20: checkpoint advances past the
        // hypothetical earlier cancel position.
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt),
                position: 5),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderShipped(orderId, "UPS", "1Z999", ShippedAt),
                position: 20, occurredUtc: ShippedAt),
            CancellationToken.None);
        var updatesAfterShip = store.UpdateCount;

        // A stale OrderCancelled at position 15 redelivers after the ship:
        // the handler returns early and the Shipped status holds.
        await projection.HandleAsync(
            Context(new OrderCancelled(orderId, "out of stock", Guid.NewGuid(), ShippedAt),
                position: 15, occurredUtc: ShippedAt),
            CancellationToken.None);

        store.UpdateCount.Should().Be(updatesAfterShip);
        (await store.GetAsync(orderId, CancellationToken.None))!
            .Status.Should().Be(OrderStatus.Shipped);
        store.Checkpoints[projection.Name].Should().Be(20);
    }

    [Fact]
    public async Task Handlers_advance_the_checkpoint_under_the_projection_name()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        projection.Name.Should().Be("order-list");
        var orderId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt),
                position: 7),
            CancellationToken.None);
        store.Checkpoints[projection.Name].Should().Be(7);

        await projection.HandleAsync(
            Context(new OrderShipped(orderId, "UPS", "1Z999", ShippedAt), position: 12),
            CancellationToken.None);
        store.Checkpoints[projection.Name].Should().Be(12);
    }

    [Fact]
    public async Task OrderCompleted_handler_sets_status_to_completed()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: PlacedAt),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderCompleted(orderId, ShippedAt), position: 2, occurredUtc: ShippedAt),
            CancellationToken.None);

        var row = await store.GetAsync(orderId, CancellationToken.None);
        row!.Status.Should().Be(OrderStatus.Completed);
        row.LastUpdatedUtc.Should().Be(ShippedAt);
    }

    [Fact]
    public async Task ShipmentReturned_after_its_ShipmentScheduled_marks_the_order_returned()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: PlacedAt),
            CancellationToken.None);
        // ShipmentScheduled records the ShipmentId -> OrderId mapping.
        await projection.HandleAsync(
            Context(new ShipmentScheduled(shipmentId, orderId, SampleAddress(), [], PlacedAt),
                position: 2),
            CancellationToken.None);

        // ShipmentReturned carries only ShipmentId; it resolves the order through
        // the mapping and marks it returned.
        await projection.HandleAsync(
            Context(new ShipmentReturned(shipmentId, "damaged", ShippedAt),
                position: 3, occurredUtc: ShippedAt),
            CancellationToken.None);

        var row = await store.GetAsync(orderId, CancellationToken.None);
        row!.IsReturned.Should().BeTrue();
        row.ReturnedUtc.Should().Be(ShippedAt);
        // The Sales status is untouched: a return is a Fulfillment fact (D5).
        row.Status.Should().Be(OrderStatus.Placed);
    }

    [Fact]
    public async Task ShipmentReturned_with_no_mapping_no_ops_and_advances_the_checkpoint()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: PlacedAt),
            CancellationToken.None);

        // No ShipmentScheduled was observed for this shipment, so the mapping is
        // absent. The handler skips the return-state update but still advances
        // the checkpoint so the event is not reprocessed.
        await projection.HandleAsync(
            Context(new ShipmentReturned(Guid.NewGuid(), "damaged", ShippedAt), position: 5),
            CancellationToken.None);

        var row = await store.GetAsync(orderId, CancellationToken.None);
        row!.IsReturned.Should().BeFalse();
        store.Checkpoints[projection.Name].Should().Be(5);
    }

    [Fact]
    public async Task ShipmentScheduled_handler_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), PlacedAt),
                position: 10),
            CancellationToken.None);

        // A ShipmentScheduled at position 10 (at the checkpoint) is a redelivery:
        // the handler returns early and records no mapping, so a later return for
        // that shipment finds nothing to resolve.
        await projection.HandleAsync(
            Context(new ShipmentScheduled(shipmentId, orderId, SampleAddress(), [], PlacedAt),
                position: 10),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new ShipmentReturned(shipmentId, "damaged", ShippedAt), position: 11),
            CancellationToken.None);

        (await store.GetAsync(orderId, CancellationToken.None))!.IsReturned.Should().BeFalse();
    }

    [Fact]
    public async Task OrderPlaced_stages_a_notification_keyed_by_the_events_customer_id()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var customerId = Guid.NewGuid();
        // A non-default tenant so the assertion proves the staged envelope carries the
        // event's metadata tenant through, not merely that the default is present. The
        // shared Metadata helper hardcodes WellKnownTenants.Default, so the metadata is
        // built inline here with the chosen tenant.
        var tenant = TenantId.From(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var context = new EventContext<OrderPlaced>(
            new OrderPlaced(Guid.NewGuid(), customerId, new Money(10m, Currency.USD), PlacedAt),
            new EventMetadata(
                EventId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid(),
                CausationId: Guid.NewGuid(),
                ActorId: Guid.Empty,
                Source: "test",
                OccurredUtc: SystemAt,
                Tenant: tenant),
            GlobalPosition: 1);

        await projection.HandleAsync(context, CancellationToken.None);

        var staged = store.StagedNotifications.Should().ContainSingle().Subject;
        staged.ProjectionName.Should().Be("order-list");
        staged.ResourceId.Should().Be(customerId.ToString());
        staged.EventName.Should().Be(nameof(OrderPlaced));
        staged.Widgets.Should().BeEmpty();
        staged.Tenant.Should().Be(tenant);
    }

    [Fact]
    public async Task OrderShipped_stages_a_notification_keyed_by_the_rows_customer_id()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, customerId, new Money(10m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: PlacedAt),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderShipped(orderId, "UPS", "1Z999", ShippedAt),
                position: 2, occurredUtc: ShippedAt),
            CancellationToken.None);

        store.StagedNotifications.Should().HaveCount(2);
        store.StagedNotifications[1].ResourceId.Should().Be(customerId.ToString());
        store.StagedNotifications[1].EventName.Should().Be(nameof(OrderShipped));
    }

    [Fact]
    public async Task OrderCancelled_stages_a_notification_keyed_by_the_rows_customer_id()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, customerId, new Money(10m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: PlacedAt),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderCancelled(orderId, "out of stock", Guid.NewGuid(), ShippedAt),
                position: 2, occurredUtc: ShippedAt),
            CancellationToken.None);

        store.StagedNotifications.Should().HaveCount(2);
        store.StagedNotifications[1].ResourceId.Should().Be(customerId.ToString());
        store.StagedNotifications[1].EventName.Should().Be(nameof(OrderCancelled));
    }

    [Fact]
    public async Task OrderCompleted_stages_a_notification_keyed_by_the_rows_customer_id()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, customerId, new Money(10m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: PlacedAt),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderCompleted(orderId, ShippedAt),
                position: 2, occurredUtc: ShippedAt),
            CancellationToken.None);

        store.StagedNotifications.Should().HaveCount(2);
        store.StagedNotifications[1].ResourceId.Should().Be(customerId.ToString());
        store.StagedNotifications[1].EventName.Should().Be(nameof(OrderCompleted));
    }

    [Fact]
    public async Task ShipmentReturned_stages_a_notification_keyed_by_the_resolved_rows_customer_id()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, customerId, new Money(10m, Currency.USD), PlacedAt),
                position: 1, occurredUtc: PlacedAt),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new ShipmentScheduled(shipmentId, orderId, SampleAddress(), [], PlacedAt),
                position: 2),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new ShipmentReturned(shipmentId, "damaged", ShippedAt),
                position: 3, occurredUtc: ShippedAt),
            CancellationToken.None);

        store.StagedNotifications.Should().HaveCount(2);
        store.StagedNotifications[1].ResourceId.Should().Be(customerId.ToString());
        store.StagedNotifications[1].EventName.Should().Be(nameof(ShipmentReturned));
    }

    [Fact]
    public async Task OrderShipped_when_the_order_was_never_placed_stages_nothing()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);

        // No row exists for this order, so there is no customer to notify.
        await projection.HandleAsync(
            Context(new OrderShipped(Guid.NewGuid(), "UPS", "1Z999", ShippedAt), position: 1),
            CancellationToken.None);

        store.StagedNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task ShipmentReturned_with_no_mapping_stages_nothing()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);

        // No ShipmentScheduled recorded a mapping, so the return resolves no
        // order and there is no customer to notify.
        await projection.HandleAsync(
            Context(new ShipmentReturned(Guid.NewGuid(), "damaged", ShippedAt), position: 1),
            CancellationToken.None);

        store.StagedNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task ShipmentScheduled_stages_nothing()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);

        // The mapping insert makes no order_list change, so no subscriber needs
        // a refresh.
        await projection.HandleAsync(
            Context(new ShipmentScheduled(Guid.NewGuid(), Guid.NewGuid(), SampleAddress(), [], PlacedAt),
                position: 1),
            CancellationToken.None);

        store.StagedNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task OrderCancelled_when_the_order_was_never_placed_stages_nothing()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);

        // The order was cancelled while still a draft: no row exists, so there
        // is no customer to notify.
        await projection.HandleAsync(
            Context(new OrderCancelled(Guid.NewGuid(), "changed mind", Guid.NewGuid(), ShippedAt),
                position: 1),
            CancellationToken.None);

        store.StagedNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task OrderCompleted_when_the_order_was_never_placed_stages_nothing()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);

        // No row exists for this order, so there is no customer to notify.
        await projection.HandleAsync(
            Context(new OrderCompleted(Guid.NewGuid(), ShippedAt), position: 1),
            CancellationToken.None);

        store.StagedNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task ShipmentReturned_with_a_mapping_but_no_order_row_stages_nothing()
    {
        var store = new InMemoryOrderListStore();
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        var shipmentId = Guid.NewGuid();
        // The mapping exists but the order_list row does not: the scheduled
        // handler inserts the mapping without requiring a row.
        await projection.HandleAsync(
            Context(new ShipmentScheduled(shipmentId, Guid.NewGuid(), SampleAddress(), [], PlacedAt),
                position: 1),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new ShipmentReturned(shipmentId, "damaged", ShippedAt), position: 2),
            CancellationToken.None);

        store.StagedNotifications.Should().BeEmpty();
    }

    private static Address SampleAddress() => new("1 Main St", "Smalltown", "12345", "US");

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
            OccurredUtc: occurredUtc,
            Tenant: WellKnownTenants.Default);
}
