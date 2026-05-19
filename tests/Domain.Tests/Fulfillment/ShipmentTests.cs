using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Domain.Tests.TestKit;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.Fulfillment;

public class ShipmentTests
{
    private static readonly Guid ShipmentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId1 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LineId2 = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string Sku1 = "SKU-1";
    private const string Sku2 = "SKU-2";
    private const string CarrierRef = "CARRIER-REF-9001";
    private static readonly Address Destination =
        new("221B Baker Street", "London", "NW1 6XE", "United Kingdom");
    private static readonly DateTime At = new(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc);

    private static ShipmentLine Line1 => new(OrderId, LineId1, Sku1, 2);
    private static ShipmentLine Line2 => new(OrderId, LineId2, Sku2, 1);

    [Fact]
    public void Schedule_creates_a_shipment_bound_to_an_order_with_contents()
    {
        var lines = new[] { Line1, Line2 };

        var shipment = Shipment.Schedule(ShipmentId, OrderId, Destination, lines, At);

        shipment.Id.Should().Be(ShipmentId);
        shipment.OrderId.Should().Be(OrderId);
        shipment.Status.Should().Be(ShipmentStatus.Scheduled);
        shipment.Lines.Should().Equal(lines);
        shipment.DequeueUncommittedEvents()
            .Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, lines, At));
    }

    [Fact]
    public void Schedule_throws_when_lines_is_empty()
    {
        var action = () => Shipment.Schedule(ShipmentId, OrderId, Destination, [], At);

        action.Should().Throw<DomainException>()
            .WithMessage("*at least one line*");
    }

    [Fact]
    public void Dispatch_transitions_a_scheduled_shipment_to_dispatched()
    {
        new AggregateTest<Shipment>()
            .Given(new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At))
            .When(s => s.Dispatch(CarrierRef, At))
            .Then(new ShipmentDispatched(ShipmentId, CarrierRef, At));
    }

    [Fact]
    public void Dispatch_throws_when_carrier_reference_is_empty()
    {
        new AggregateTest<Shipment>()
            .Given(new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At))
            .When(s => s.Dispatch("", At))
            .ThenThrows<DomainException>()
            .WithMessage("*carrier reference must be non-empty*");
    }

    [Fact]
    public void Dispatch_throws_when_shipment_is_already_dispatched()
    {
        new AggregateTest<Shipment>()
            .Given(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At),
                new ShipmentDispatched(ShipmentId, CarrierRef, At))
            .When(s => s.Dispatch(CarrierRef, At))
            .ThenThrows<DomainException>()
            .WithMessage("*shipment is Dispatched*");
    }

    [Fact]
    public void Dispatch_throws_when_shipment_is_already_delivered()
    {
        new AggregateTest<Shipment>()
            .Given(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At),
                new ShipmentDispatched(ShipmentId, CarrierRef, At),
                new ShipmentDelivered(ShipmentId, At))
            .When(s => s.Dispatch(CarrierRef, At))
            .ThenThrows<DomainException>()
            .WithMessage("*shipment is Delivered*");
    }

    [Fact]
    public void Dispatch_throws_when_shipment_is_already_returned()
    {
        new AggregateTest<Shipment>()
            .Given(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At),
                new ShipmentDispatched(ShipmentId, CarrierRef, At),
                new ShipmentDelivered(ShipmentId, At),
                new ShipmentReturned(ShipmentId, "Damaged on arrival", At))
            .When(s => s.Dispatch(CarrierRef, At))
            .ThenThrows<DomainException>()
            .WithMessage("*shipment is Returned*");
    }

    [Fact]
    public void Deliver_transitions_a_dispatched_shipment_to_delivered()
    {
        new AggregateTest<Shipment>()
            .Given(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At),
                new ShipmentDispatched(ShipmentId, CarrierRef, At))
            .When(s => s.Deliver(At))
            .Then(new ShipmentDelivered(ShipmentId, At));
    }

    [Fact]
    public void Deliver_throws_when_shipment_is_still_scheduled()
    {
        new AggregateTest<Shipment>()
            .Given(new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At))
            .When(s => s.Deliver(At))
            .ThenThrows<DomainException>()
            .WithMessage("*shipment is Scheduled*");
    }

    [Fact]
    public void Deliver_throws_when_shipment_is_already_delivered()
    {
        new AggregateTest<Shipment>()
            .Given(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At),
                new ShipmentDispatched(ShipmentId, CarrierRef, At),
                new ShipmentDelivered(ShipmentId, At))
            .When(s => s.Deliver(At))
            .ThenThrows<DomainException>()
            .WithMessage("*shipment is Delivered*");
    }

    [Fact]
    public void Deliver_throws_when_shipment_is_already_returned()
    {
        new AggregateTest<Shipment>()
            .Given(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At),
                new ShipmentDispatched(ShipmentId, CarrierRef, At),
                new ShipmentDelivered(ShipmentId, At),
                new ShipmentReturned(ShipmentId, "Damaged on arrival", At))
            .When(s => s.Deliver(At))
            .ThenThrows<DomainException>()
            .WithMessage("*shipment is Returned*");
    }

    [Fact]
    public void Return_transitions_a_delivered_shipment_to_returned()
    {
        new AggregateTest<Shipment>()
            .Given(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At),
                new ShipmentDispatched(ShipmentId, CarrierRef, At),
                new ShipmentDelivered(ShipmentId, At))
            .When(s => s.Return("Damaged on arrival", At))
            .Then(new ShipmentReturned(ShipmentId, "Damaged on arrival", At));
    }

    [Fact]
    public void Return_throws_when_reason_is_empty()
    {
        new AggregateTest<Shipment>()
            .Given(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At),
                new ShipmentDispatched(ShipmentId, CarrierRef, At),
                new ShipmentDelivered(ShipmentId, At))
            .When(s => s.Return("", At))
            .ThenThrows<DomainException>()
            .WithMessage("*reason must be non-empty*");
    }

    [Fact]
    public void Return_throws_when_shipment_is_still_scheduled()
    {
        new AggregateTest<Shipment>()
            .Given(new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At))
            .When(s => s.Return("Damaged on arrival", At))
            .ThenThrows<DomainException>()
            .WithMessage("*shipment is Scheduled*");
    }

    [Fact]
    public void Return_throws_when_shipment_is_dispatched_but_not_delivered()
    {
        new AggregateTest<Shipment>()
            .Given(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At),
                new ShipmentDispatched(ShipmentId, CarrierRef, At))
            .When(s => s.Return("Damaged on arrival", At))
            .ThenThrows<DomainException>()
            .WithMessage("*shipment is Dispatched*");
    }

    [Fact]
    public void Return_throws_when_shipment_is_already_returned()
    {
        new AggregateTest<Shipment>()
            .Given(
                new ShipmentScheduled(ShipmentId, OrderId, Destination, [Line1], At),
                new ShipmentDispatched(ShipmentId, CarrierRef, At),
                new ShipmentDelivered(ShipmentId, At),
                new ShipmentReturned(ShipmentId, "Damaged on arrival", At))
            .When(s => s.Return("Second return", At))
            .ThenThrows<DomainException>()
            .WithMessage("*shipment is Returned*");
    }

    [Fact]
    public void Lifecycle_happy_path_schedules_dispatches_delivers_then_returns()
    {
        var shipment = Shipment.Schedule(ShipmentId, OrderId, Destination, [Line1, Line2], At);
        shipment.Dispatch(CarrierRef, At);
        shipment.Deliver(At);
        shipment.Return("Customer returned", At);

        shipment.Status.Should().Be(ShipmentStatus.Returned);
        shipment.DequeueUncommittedEvents().Should().HaveCount(4);
    }

    [Fact]
    public void ShipmentLine_uses_reference_equality_so_two_instances_with_the_same_fields_are_distinct()
    {
        var lineA = new ShipmentLine(OrderId, LineId1, Sku1, 2);
        var lineB = new ShipmentLine(OrderId, LineId1, Sku1, 2);

        lineA.Should().NotBeSameAs(lineB);
        lineA.Equals(lineB).Should().BeFalse();
    }
}
