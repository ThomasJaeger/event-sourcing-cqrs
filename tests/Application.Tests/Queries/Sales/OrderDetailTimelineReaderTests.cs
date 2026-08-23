using System.Text.Json;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.Versioning;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Queries.Sales;

// Reading an identifier back out of an order's timeline. OrderDetailTimelineReader does not
// exist yet; these facts drive it into being.
//
// Why the timeline is the surface. The fulfillment process manager mints the payment id and
// the shipment id itself, so a caller that placed the order never sees either one, and no read
// model exposes an order to shipment lookup: the two mapping tables are keyed the other way and
// are marked private to their projection. The order detail view is the one public query whose
// result carries the ids, because every observed event is recorded there with its full payload.
//
// Why the requested type must match the event type asked for. The shared options skip unmapped
// members and leave absent ones at their defaults, so deserialising one event's payload into
// another event's type yields a half populated object rather than a fault. A reader that let
// that through would hand a caller an empty guid and call it an answer.
//
// The rows here are built by serialising real events with the same options the projection uses,
// so the facts run against the payload shape a reader meets in the running system.
public class OrderDetailTimelineReaderTests
{
    private static readonly DateTime At = new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Options = EventStoreJsonOptions.Create();

    // Fact 1. The row is found by its event type and its payload comes back with its fields
    // intact, not merely non null.
    [Fact]
    public void A_single_matching_row_returns_its_payload_deserialised()
    {
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var view = ViewWith(orderId, Row(orderId, 5, Scheduled(shipmentId, orderId)));

        var payload = OrderDetailTimelineReader.ReadFirst<ShipmentScheduled>(
            view, nameof(ShipmentScheduled), Options);

        payload.ShipmentId.Should().Be(shipmentId);
        payload.OrderId.Should().Be(orderId);
        payload.ScheduledUtc.Should().Be(At);
    }

    // Fact 2. Nothing matched, so the reader says which event type it looked for rather than
    // returning a default or faulting on an empty sequence.
    [Fact]
    public void A_timeline_with_no_matching_row_fails_naming_the_event_type()
    {
        var orderId = Guid.NewGuid();
        var view = ViewWith(orderId, Row(orderId, 5, Authorized(Guid.NewGuid(), orderId)));

        var act = () => OrderDetailTimelineReader.ReadFirst<ShipmentScheduled>(
            view, nameof(ShipmentScheduled), Options);

        var thrown = act.Should().Throw<InvalidOperationException>();
        thrown.Which.Message.Should().Contain(nameof(ShipmentScheduled));
    }

    // Fact 3. The timeline arrives ordered by global position ascending, so the first match in
    // that order is the earliest observed occurrence. The fact names the row it expects rather
    // than asserting that some row came back.
    [Fact]
    public void Several_matching_rows_resolve_to_the_earliest_by_global_position()
    {
        var orderId = Guid.NewGuid();
        var earliest = Guid.NewGuid();
        var later = Guid.NewGuid();
        var view = ViewWith(
            orderId,
            Row(orderId, 7, Scheduled(earliest, orderId)),
            Row(orderId, 12, Scheduled(later, orderId)));

        var payload = OrderDetailTimelineReader.ReadFirst<ShipmentScheduled>(
            view, nameof(ShipmentScheduled), Options);

        payload.ShipmentId.Should().Be(earliest);
        payload.ShipmentId.Should().NotBe(later);
    }

    // Fact 4. A real payment payload asked for as a shipment. Skipping unmapped members would
    // otherwise return a shipment whose every field defaulted, so the reader refuses and names
    // both the type it was asked for and the type it was asked to produce.
    [Fact]
    public void A_requested_type_that_does_not_match_the_event_type_fails_clearly()
    {
        var orderId = Guid.NewGuid();
        var view = ViewWith(orderId, Row(orderId, 5, Authorized(Guid.NewGuid(), orderId)));

        var act = () => OrderDetailTimelineReader.ReadFirst<ShipmentScheduled>(
            view, nameof(PaymentAuthorized), Options);

        var thrown = act.Should().Throw<InvalidOperationException>();
        thrown.Which.Message.Should().Contain(nameof(PaymentAuthorized));
        thrown.Which.Message.Should().Contain(nameof(ShipmentScheduled));
    }

    // Arrangement shared by the facts above.

    private static ShipmentScheduled Scheduled(Guid shipmentId, Guid orderId)
        => new(
            ShipmentId: shipmentId,
            OrderId: orderId,
            Destination: new Address("1 Test Way", "Testville", "00001", "US"),
            Lines: [new ShipmentLine(orderId, Guid.NewGuid(), "SKU-1", 1)],
            ScheduledUtc: At);

    private static PaymentAuthorized Authorized(Guid paymentId, Guid orderId)
        => new(
            PaymentId: paymentId,
            OrderId: orderId,
            Amount: new Money(100m, Currency.USD),
            PaymentMethodReference: "test-method",
            AuthorizedUtc: At);

    // Mirrors what OrderDetailProjection writes: the event's own type name, and the event
    // serialised whole with the shared options.
    private static OrderDetailTimelineRow Row(Guid orderId, long globalPosition, IDomainEvent payload)
        => new(
            OrderId: orderId,
            GlobalPosition: globalPosition,
            EventType: payload.GetType().Name,
            OccurredUtc: At,
            Payload: JsonSerializer.Serialize(payload, payload.GetType(), Options));

    private static OrderDetailView ViewWith(Guid orderId, params OrderDetailTimelineRow[] timeline)
        => new(Header(orderId), [], timeline);

    private static OrderDetailRow Header(Guid orderId)
        => new(
            OrderId: orderId,
            CustomerId: Guid.NewGuid(),
            Status: OrderStatus.Placed,
            PlacedUtc: At,
            ShippedUtc: null,
            CancelledUtc: null,
            CompletedUtc: null,
            ReturnedUtc: null,
            Total: new Money(100m, Currency.USD),
            ShippingAddress: null,
            LastUpdatedUtc: At);
}
