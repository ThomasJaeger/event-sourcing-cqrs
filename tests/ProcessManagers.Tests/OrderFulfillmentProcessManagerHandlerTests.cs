using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.ProcessManagers.Tests;

// Handler-level coverage for OrderFulfillmentProcessManagerHandler (commit 21):
// the happy path through all four handlers, the two redelivery cases R2's
// re-dispatch shape implies, the per-line idempotency-key format, and the
// Shipment-load correlation that recovers OrderId from an event lacking it.
// Compensation branches arrive at commits 23-24 via the harness's OutcomeFor.
public sealed class OrderFulfillmentProcessManagerHandlerTests
{
    private static readonly DateTime Now = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Address Destination = new("1 Main St", "Smalltown", "12345", "US");

    [Fact]
    public async Task Happy_path_runs_through_all_four_handlers_to_completed()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        var line1 = Guid.NewGuid();
        var line2 = Guid.NewGuid();
        harness.MapSku("SKU-1", Guid.NewGuid());
        harness.MapSku("SKU-2", Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (line1, "SKU-1", 2), (line2, "SKU-2", 3)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(50m), Now));
        await harness.Receive(new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(50m), "card", Now));

        var afterPayment = await harness.LoadPm(orderId);
        afterPayment!.State.Should().Be(OrderFulfillmentState.AwaitingDispatch);
        var shipmentId = afterPayment.ShipmentId;
        await harness.SeedShipment(Shipment.Schedule(
            shipmentId, orderId, Destination, [new ShipmentLine(orderId, line1, "SKU-1", 2)], Now));

        await harness.Receive(new ShipmentDispatched(shipmentId, "carrier-ref", Now));
        await harness.Receive(new ShipmentDelivered(shipmentId, Now));

        var stream = PmStream(orderId);
        harness.Dispatched.Where(d => d.Command is AuthorizePayment).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:authorize-payment");
        harness.Dispatched.Where(d => d.Command is ReserveInventory).Select(d => d.IdempotencyKey)
            .Should().BeEquivalentTo(
                new[] { $"{stream.Value}:reserve:{line1:N}", $"{stream.Value}:reserve:{line2:N}" });
        harness.Dispatched.Where(d => d.Command is ScheduleShipment).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:schedule-shipment");
        (await harness.LoadPm(orderId))!.State.Should().Be(OrderFulfillmentState.Completed);
    }

    [Fact]
    public async Task OrderPlaced_redelivery_keeps_pm_version_stable_and_reuses_the_authorize_key()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-1", 1)));

        var placed = new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now);
        await harness.Receive(placed);
        var versionAfterFirst = (await harness.LoadPm(orderId))!.Version;
        await harness.Receive(placed);
        var versionAfterSecond = (await harness.LoadPm(orderId))!.Version;

        // The second delivery hits the NotStarted guard, so no new transition and
        // no version bump; R2 still re-dispatches with the same key.
        versionAfterSecond.Should().Be(versionAfterFirst);
        var stream = PmStream(orderId);
        harness.Dispatched.Should().HaveCount(2);
        harness.Dispatched.Should().OnlyContain(d => d.Command is AuthorizePayment);
        harness.Dispatched.Select(d => d.IdempotencyKey).Should().AllBe($"{stream.Value}:authorize-payment");
    }

    [Fact]
    public async Task PaymentAuthorized_redelivery_does_not_duplicate_reservations_and_reuses_the_schedule_key()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        harness.MapSku("SKU-1", Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-1", 2)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(40m), Now));
        var auth = new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(40m), "card", Now);
        await harness.Receive(auth);
        var reservationsAfterFirst = (await harness.LoadPm(orderId))!.Reservations.Count;
        await harness.Receive(auth);

        (await harness.LoadPm(orderId))!.Reservations.Count.Should().Be(reservationsAfterFirst);
        var stream = PmStream(orderId);
        harness.Dispatched.Where(d => d.Command is ReserveInventory).Should().ContainSingle();
        harness.Dispatched.Where(d => d.Command is ScheduleShipment).Select(d => d.IdempotencyKey)
            .Should().HaveCount(2).And.AllBe($"{stream.Value}:schedule-shipment");
    }

    [Fact]
    public async Task Each_reserve_inventory_dispatch_carries_the_per_line_idempotency_key()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        var lineA = Guid.NewGuid();
        var lineB = Guid.NewGuid();
        var lineC = Guid.NewGuid();
        harness.MapSku("SKU-A", Guid.NewGuid());
        harness.MapSku("SKU-B", Guid.NewGuid());
        harness.MapSku("SKU-C", Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (lineA, "SKU-A", 1), (lineB, "SKU-B", 1), (lineC, "SKU-C", 1)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(60m), Now));
        await harness.Receive(new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(60m), "card", Now));

        var stream = PmStream(orderId);
        harness.Dispatched.Where(d => d.Command is ReserveInventory).Select(d => d.IdempotencyKey)
            .Should().BeEquivalentTo(new[]
            {
                $"{stream.Value}:reserve:{lineA:N}",
                $"{stream.Value}:reserve:{lineB:N}",
                $"{stream.Value}:reserve:{lineC:N}"
            });
    }

    [Fact]
    public async Task ShipmentDispatched_correlates_the_pm_through_a_shipment_load()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        var line1 = Guid.NewGuid();
        harness.MapSku("SKU-1", Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (line1, "SKU-1", 1)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(25m), Now));
        await harness.Receive(new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(25m), "card", Now));
        var shipmentId = (await harness.LoadPm(orderId))!.ShipmentId;
        await harness.SeedShipment(Shipment.Schedule(
            shipmentId, orderId, Destination, [new ShipmentLine(orderId, line1, "SKU-1", 1)], Now));

        // ShipmentDispatched carries no OrderId; the handler recovers it from the shipment.
        await harness.Receive(new ShipmentDispatched(shipmentId, "carrier-ref", Now));

        (await harness.LoadPm(orderId))!.State.Should().Be(OrderFulfillmentState.AwaitingDelivery);
    }

    [Fact]
    public async Task AuthorizePayment_failure_cancels_the_order_and_voids_nothing()
    {
        var harness = new OrderFulfillmentTestHarness();
        harness.Bus.OutcomeFor = cmd => cmd is AuthorizePayment
            ? CommandOutcome.Failed(new DomainException("payment declined"))
            : CommandOutcome.Success();
        var orderId = Guid.NewGuid();
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-1", 1)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now));

        var stream = PmStream(orderId);
        harness.Dispatched.Where(d => d.Command is CancelOrder).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:cancel-order");
        // Branch 1 voids nothing: the payment was never authorized.
        harness.Dispatched.Should().NotContain(d => d.Command is VoidPayment);
        (await harness.LoadPm(orderId))!.State.Should().Be(OrderFulfillmentState.Cancelled);
    }

    [Fact]
    public async Task AuthorizePayment_failure_redelivery_stays_terminal_without_re_dispatching()
    {
        var harness = new OrderFulfillmentTestHarness();
        harness.Bus.OutcomeFor = cmd => cmd is AuthorizePayment
            ? CommandOutcome.Failed(new DomainException("payment declined"))
            : CommandOutcome.Success();
        var orderId = Guid.NewGuid();
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-1", 1)));

        var placed = new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now);
        await harness.Receive(placed);
        var versionAfterFirst = (await harness.LoadPm(orderId))!.Version;
        await harness.Receive(placed);
        var pm = await harness.LoadPm(orderId);

        // The Cancelled terminal makes the second delivery a no-op: the
        // AwaitingPayment guard fails, so no re-record and no re-dispatch.
        pm!.State.Should().Be(OrderFulfillmentState.Cancelled);
        pm.Version.Should().Be(versionAfterFirst);
        harness.Dispatched.Where(d => d.Command is AuthorizePayment).Should().ContainSingle();
        harness.Dispatched.Where(d => d.Command is CancelOrder).Should().ContainSingle();
    }

    [Fact]
    public async Task All_reservations_failing_voids_the_payment_and_cancels_the_order()
    {
        var harness = new OrderFulfillmentTestHarness();
        // No MapSku: every SKU lookup returns null, so every line fails to reserve.
        var orderId = Guid.NewGuid();
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-1", 1), (Guid.NewGuid(), "SKU-2", 2)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(40m), Now));
        await harness.Receive(new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(40m), "card", Now));

        var stream = PmStream(orderId);
        harness.Dispatched.Where(d => d.Command is VoidPayment).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:void-payment");
        harness.Dispatched.Where(d => d.Command is CancelOrder).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:cancel-order");
        // The workflow never reached AwaitingDispatch, so no shipment is scheduled.
        harness.Dispatched.Should().NotContain(d => d.Command is ScheduleShipment);
        (await harness.LoadPm(orderId))!.State.Should().Be(OrderFulfillmentState.Cancelled);
    }

    private static Order BuildOrder(Guid orderId, params (Guid LineId, string Sku, int Qty)[] lines)
    {
        var order = Order.Draft(orderId, Guid.NewGuid(), Now);
        foreach (var (lineId, sku, qty) in lines)
        {
            order.AddLine(lineId, sku, qty, Usd(10m), Now);
        }
        order.SetShippingAddress(Destination, Now);
        order.Place(Now);
        return order;
    }

    private static StreamId PmStream(Guid orderId) =>
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, orderId);

    private static Money Usd(decimal amount) => new(amount, Currency.USD);
}
