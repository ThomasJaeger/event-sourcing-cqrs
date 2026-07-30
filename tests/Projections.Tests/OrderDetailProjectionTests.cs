using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Projections.OrderDetail;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
    private static readonly Address Addr = new("12 Main St", "Portland", "97201", "US");

    [Fact]
    public async Task OrderDrafted_creates_header_as_draft_and_appends_timeline()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, customerId, At, "web"), position: 1), CancellationToken.None);

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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);

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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);
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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);

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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);

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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);

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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);

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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);

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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 7, occurredUtc: SystemAt),
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
        JsonSerializer.Deserialize<OrderPlaced>(entry.Payload, EventStoreJsonOptions.Create()).Should().Be(placed);
    }

    [Fact]
    public async Task Handler_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        // The draft commits at position 10, so the checkpoint sits at 10.
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 10), CancellationToken.None);

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
        var drafted = new OrderDrafted(orderId, Guid.NewGuid(), At, "web");

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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);
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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);
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
            Context(new OrderDrafted(order1, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new OrderDrafted(order2, Guid.NewGuid(), At, "web"), position: 2), CancellationToken.None);

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
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 10), CancellationToken.None);

        // A redelivered OrderLineAdded at position 10 returns early, so no line lands.
        await projection.HandleAsync(
            Context(new OrderLineAdded(orderId, Guid.NewGuid(), "SKU-1", 1, new Money(5m, Currency.USD), At),
                position: 10),
            CancellationToken.None);

        (await store.GetLinesAsync(orderId, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ShipmentDispatched_appends_timeline_when_the_shipment_is_mapped()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var shipmentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new ShipmentScheduled(shipmentId, orderId, Addr, [], At), position: 1),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new ShipmentDispatched(shipmentId, "1Z-REF", At), position: 2), CancellationToken.None);

        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(ShipmentDispatched));
    }

    [Fact]
    public async Task ShipmentDelivered_appends_timeline_when_the_shipment_is_mapped()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var shipmentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new ShipmentScheduled(shipmentId, orderId, Addr, [], At), position: 1),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new ShipmentDelivered(shipmentId, At), position: 2), CancellationToken.None);

        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(ShipmentDelivered));
    }

    [Fact]
    public async Task ShipmentReturned_marks_returned_and_appends_timeline_when_mapped()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var shipmentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new ShipmentScheduled(shipmentId, orderId, Addr, [], At), position: 2),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new ShipmentReturned(shipmentId, "damaged", At), position: 3), CancellationToken.None);

        var header = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        header.ReturnedUtc.Should().Be(At);
        header.Status.Should().Be(OrderStatus.Draft);
        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(ShipmentReturned));
    }

    [Fact]
    public async Task PaymentAuthorized_records_the_mapping_and_appends_timeline()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new PaymentAuthorized(paymentId, orderId, new Money(50m, Currency.USD), "auth-ref", At),
                position: 1),
            CancellationToken.None);

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderIdByPaymentIdAsync(paymentId, CancellationToken.None)).Should().Be(orderId);
        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(PaymentAuthorized));
    }

    [Fact]
    public async Task PaymentCaptured_appends_timeline_when_the_payment_is_mapped()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new PaymentAuthorized(paymentId, orderId, new Money(50m, Currency.USD), "auth-ref", At),
                position: 1),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new PaymentCaptured(paymentId, new Money(50m, Currency.USD), At), position: 2),
            CancellationToken.None);

        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(PaymentCaptured));
    }

    [Fact]
    public async Task PaymentRefunded_appends_timeline_when_the_payment_is_mapped()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new PaymentAuthorized(paymentId, orderId, new Money(50m, Currency.USD), "auth-ref", At),
                position: 1),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new PaymentRefunded(paymentId, new Money(50m, Currency.USD), "returned", At), position: 2),
            CancellationToken.None);

        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(PaymentRefunded));
    }

    [Fact]
    public async Task PaymentVoided_appends_timeline_when_the_payment_is_mapped()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new PaymentAuthorized(paymentId, orderId, new Money(50m, Currency.USD), "auth-ref", At),
                position: 1),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new PaymentVoided(paymentId, "expired", At), position: 2), CancellationToken.None);

        (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Should().Contain(t => t.EventType == nameof(PaymentVoided));
    }

    [Fact]
    public async Task ShipmentDispatched_with_no_mapping_no_ops_and_advances_the_checkpoint()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);

        await projection.HandleAsync(
            Context(new ShipmentDispatched(Guid.NewGuid(), "1Z-REF", At), position: 5),
            CancellationToken.None);

        store.Checkpoints[projection.Name].Should().Be(5);
    }

    [Fact]
    public async Task ShipmentDelivered_with_no_mapping_no_ops_and_advances_the_checkpoint()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);

        await projection.HandleAsync(
            Context(new ShipmentDelivered(Guid.NewGuid(), At), position: 5), CancellationToken.None);

        store.Checkpoints[projection.Name].Should().Be(5);
    }

    [Fact]
    public async Task ShipmentReturned_with_no_mapping_leaves_the_header_unmarked()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);

        // No ShipmentScheduled recorded the mapping, so the return cannot resolve an
        // order; the header's returned_utc stays null and the checkpoint advances.
        await projection.HandleAsync(
            Context(new ShipmentReturned(Guid.NewGuid(), "damaged", At), position: 2),
            CancellationToken.None);

        (await store.GetHeaderAsync(orderId, CancellationToken.None))!.ReturnedUtc.Should().BeNull();
        store.Checkpoints[projection.Name].Should().Be(2);
    }

    [Fact]
    public async Task PaymentCaptured_with_no_mapping_no_ops_and_advances_the_checkpoint()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);

        await projection.HandleAsync(
            Context(new PaymentCaptured(Guid.NewGuid(), new Money(50m, Currency.USD), At), position: 5),
            CancellationToken.None);

        store.Checkpoints[projection.Name].Should().Be(5);
    }

    [Fact]
    public async Task PaymentRefunded_with_no_mapping_no_ops_and_advances_the_checkpoint()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);

        await projection.HandleAsync(
            Context(new PaymentRefunded(Guid.NewGuid(), new Money(50m, Currency.USD), "returned", At),
                position: 5),
            CancellationToken.None);

        store.Checkpoints[projection.Name].Should().Be(5);
    }

    [Fact]
    public async Task PaymentVoided_with_no_mapping_no_ops_and_advances_the_checkpoint()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);

        await projection.HandleAsync(
            Context(new PaymentVoided(Guid.NewGuid(), "expired", At), position: 5), CancellationToken.None);

        store.Checkpoints[projection.Name].Should().Be(5);
    }

    [Fact]
    public async Task PaymentAuthorized_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        // The draft commits at position 10, so the checkpoint sits at 10.
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 10), CancellationToken.None);

        // PaymentAuthorized redelivered at position 10 returns early, so the
        // payment-to-order mapping is never recorded.
        await projection.HandleAsync(
            Context(new PaymentAuthorized(paymentId, orderId, new Money(50m, Currency.USD), "auth-ref", At),
                position: 10),
            CancellationToken.None);

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderIdByPaymentIdAsync(paymentId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task OrderPlaced_stages_an_order_notification_keyed_by_the_order_id()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), position: 1), CancellationToken.None);

        await projection.HandleAsync(
            Context(new OrderPlaced(orderId, Guid.NewGuid(), new Money(125m, Currency.USD), At), position: 2),
            CancellationToken.None);

        // OrderDetail is a v1 SignalR subscriber: the draft and the placement each
        // stage one order-keyed notification for the hub to route to order:{orderId}.
        store.StagedNotifications.Should().HaveCount(2);
        var placed = store.StagedNotifications[1];
        placed.ProjectionName.Should().Be(projection.Name);
        placed.ResourceId.Should().Be(orderId.ToString());
        placed.EventName.Should().Be(nameof(OrderPlaced));
        placed.Widgets.Should().BeEmpty();
    }

    [Fact]
    public async Task OrderDrafted_stages_the_notification_carrying_the_event_metadata_tenant()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        // A non-default tenant so the assertion proves the staged envelope carries the
        // event's metadata tenant through, not merely that the default is present. The
        // shared Metadata helper hardcodes WellKnownTenants.Default, so the metadata is
        // built inline here with the chosen tenant.
        var tenant = TenantId.From(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var context = new EventContext<OrderDrafted>(
            new OrderDrafted(Guid.NewGuid(), Guid.NewGuid(), At, "web"),
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
        staged.Tenant.Should().Be(tenant);
    }

    [Fact]
    public async Task A_payment_event_with_no_mapping_stages_no_notification()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);

        // No PaymentAuthorized recorded the mapping, so PaymentCaptured cannot
        // resolve an order: it writes nothing and stages nothing.
        await projection.HandleAsync(
            Context(new PaymentCaptured(Guid.NewGuid(), new Money(50m, Currency.USD), At), position: 1),
            CancellationToken.None);

        store.StagedNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resolved_payment_event_stages_keyed_by_the_resolved_order_id()
    {
        var store = new InMemoryOrderDetailStore();
        var projection = Projection(store);
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new PaymentAuthorized(paymentId, orderId, new Money(50m, Currency.USD), "auth-ref", At),
                position: 1),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new PaymentCaptured(paymentId, new Money(50m, Currency.USD), At), position: 2),
            CancellationToken.None);

        // PaymentAuthorized (direct) and PaymentCaptured (resolved through the
        // payment-to-order lookup) both stage, keyed by the resolved OrderId.
        store.StagedNotifications.Should().HaveCount(2);
        store.StagedNotifications.Should().OnlyContain(n => n.ResourceId == orderId.ToString());
        store.StagedNotifications[1].EventName.Should().Be(nameof(PaymentCaptured));
    }

    private static OrderDetailProjection Projection(InMemoryOrderDetailStore store)
        => new(store, EventStoreJsonOptions.Create(), NullLogger<OrderDetailProjection>.Instance);

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
