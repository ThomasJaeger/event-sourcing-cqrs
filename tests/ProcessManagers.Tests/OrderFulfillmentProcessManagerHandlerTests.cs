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
        harness.Dispatched.Where(d => d.Command is MarkOrderCompleted).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:mark-completed");
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

    [Fact]
    public async Task Partial_reservation_releases_the_reserved_line_voids_and_cancels()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        var reserved = Guid.NewGuid();
        var failed = Guid.NewGuid();
        harness.MapSku("SKU-OK", Guid.NewGuid());
        // SKU-MISSING is unmapped, so its line fails to reserve.
        await harness.SeedOrder(BuildOrder(orderId, (reserved, "SKU-OK", 1), (failed, "SKU-MISSING", 2)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(40m), Now));
        await harness.Receive(new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(40m), "card", Now));

        var stream = PmStream(orderId);
        harness.Dispatched.Where(d => d.Command is ReleaseInventory).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:release:{reserved:N}");
        harness.Dispatched.Where(d => d.Command is VoidPayment).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:void-payment");
        harness.Dispatched.Where(d => d.Command is CancelOrder).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:cancel-order");
        harness.Dispatched.Should().NotContain(d => d.Command is ScheduleShipment);
        (await harness.LoadPm(orderId))!.State.Should().Be(OrderFulfillmentState.Cancelled);
    }

    [Fact]
    public async Task ScheduleShipment_failure_releases_all_lines_voids_and_cancels()
    {
        var harness = new OrderFulfillmentTestHarness();
        harness.Bus.OutcomeFor = cmd => cmd is ScheduleShipment
            ? CommandOutcome.Failed(new DomainException("carrier rejected the shipment"))
            : CommandOutcome.Success();
        var orderId = Guid.NewGuid();
        var lineA = Guid.NewGuid();
        var lineB = Guid.NewGuid();
        harness.MapSku("SKU-A", Guid.NewGuid());
        harness.MapSku("SKU-B", Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (lineA, "SKU-A", 1), (lineB, "SKU-B", 2)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(60m), Now));
        await harness.Receive(new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(60m), "card", Now));

        var stream = PmStream(orderId);
        // Both reserved lines released; the failed ScheduleShipment dispatched once.
        harness.Dispatched.Where(d => d.Command is ReleaseInventory).Select(d => d.IdempotencyKey)
            .Should().BeEquivalentTo(new[]
            {
                $"{stream.Value}:release:{lineA:N}",
                $"{stream.Value}:release:{lineB:N}"
            });
        harness.Dispatched.Where(d => d.Command is ScheduleShipment).Should().ContainSingle();
        harness.Dispatched.Where(d => d.Command is VoidPayment).Should().ContainSingle();
        harness.Dispatched.Where(d => d.Command is CancelOrder).Should().ContainSingle();
        (await harness.LoadPm(orderId))!.State.Should().Be(OrderFulfillmentState.Cancelled);
    }

    [Fact]
    public async Task Partial_reservation_redelivery_stays_terminal_without_re_dispatching()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        harness.MapSku("SKU-OK", Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-OK", 1), (Guid.NewGuid(), "SKU-MISSING", 2)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(40m), Now));
        var auth = new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(40m), "card", Now);
        await harness.Receive(auth);
        var versionAfterFirst = (await harness.LoadPm(orderId))!.Version;
        await harness.Receive(auth);
        var pm = await harness.LoadPm(orderId);

        // Cancelled is terminal: the second delivery guards out, no re-record and
        // no re-dispatch of the compensation commands.
        pm!.State.Should().Be(OrderFulfillmentState.Cancelled);
        pm.Version.Should().Be(versionAfterFirst);
        harness.Dispatched.Where(d => d.Command is ReleaseInventory).Should().ContainSingle();
        harness.Dispatched.Where(d => d.Command is VoidPayment).Should().ContainSingle();
        harness.Dispatched.Where(d => d.Command is CancelOrder).Should().ContainSingle();
    }

    [Fact]
    public async Task Schedules_on_enter_and_cancels_on_leave_through_the_happy_path()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        var line = Guid.NewGuid();
        harness.MapSku("SKU-1", Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (line, "SKU-1", 1)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now));
        harness.DelayQueue.Scheduled.Where(s => s.Step == "await-payment-timeout").Should().ContainSingle();

        await harness.Receive(new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(30m), "card", Now));
        harness.DelayQueue.Cancelled.Where(c => c.Step == "await-payment-timeout").Should().ContainSingle();
        harness.DelayQueue.Scheduled.Where(s => s.Step == "await-dispatch-timeout").Should().ContainSingle();

        var shipmentId = (await harness.LoadPm(orderId))!.ShipmentId;
        await harness.SeedShipment(Shipment.Schedule(
            shipmentId, orderId, Destination, [new ShipmentLine(orderId, line, "SKU-1", 1)], Now));
        await harness.Receive(new ShipmentDispatched(shipmentId, "carrier-ref", Now));
        harness.DelayQueue.Cancelled.Where(c => c.Step == "await-dispatch-timeout").Should().ContainSingle();
    }

    [Fact]
    public async Task AwaitingPayment_timeout_cancels_the_order()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-1", 1)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now));
        await harness.DispatchTimeoutAwaitingPayment(orderId);

        var stream = PmStream(orderId);
        harness.Dispatched.Where(d => d.Command is CancelOrder).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:cancel-order");
        harness.Dispatched.Should().NotContain(d => d.Command is VoidPayment);
        (await harness.LoadPm(orderId))!.State.Should().Be(OrderFulfillmentState.Cancelled);
    }

    [Fact]
    public async Task AwaitingPayment_timeout_loads_the_pm_under_the_resurfaced_tenant_and_cancels_it()
    {
        // The reversal of Option 1: the timeout loads the PM under the resurfaced command's tenant from
        // the accessor, finds it at the tenant-form stream, and cancels it. The normal-flow OrderPlaced
        // lands the PM at the tenant-form stream, the accessor carries the same tenant, and the
        // compensation command's metadata carries it too. The tenant reaches the accessor the way it
        // does in production: off the causing event the scheduled timeout carries.
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        var otherTenant = TenantId.From(Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-1", 1)));
        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now), MetadataForTenant(otherTenant));

        await harness.DispatchTimeoutAwaitingPayment(orderId);

        var cancel = harness.Dispatched.Where(d => d.Command is CancelOrder).Should().ContainSingle().Which;
        var pm = await harness.LoadPmForTenant(otherTenant, orderId);
        pm.Should().NotBeNull();
        pm!.State.Should().Be(OrderFulfillmentState.Cancelled);
        cancel.Metadata.Tenant.Should().Be(otherTenant);
    }

    [Fact]
    public async Task AwaitingPayment_timeout_after_payment_authorized_is_a_no_op()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        harness.MapSku("SKU-1", Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-1", 1)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now));
        await harness.Receive(new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(30m), "card", Now));
        var stateBefore = (await harness.LoadPm(orderId))!.State;
        var dispatchedBefore = harness.Dispatched.Count;

        await harness.DispatchTimeoutAwaitingPayment(orderId);

        // The PM advanced past AwaitingPayment; the late timeout state-guards and no-ops.
        (await harness.LoadPm(orderId))!.State.Should().Be(stateBefore);
        harness.Dispatched.Count.Should().Be(dispatchedBefore);
    }

    [Fact]
    public async Task AwaitingDispatch_timeout_releases_voids_and_cancels()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        var line = Guid.NewGuid();
        harness.MapSku("SKU-1", Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (line, "SKU-1", 2)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now));
        await harness.Receive(new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(30m), "card", Now));
        (await harness.LoadPm(orderId))!.State.Should().Be(OrderFulfillmentState.AwaitingDispatch);

        await harness.DispatchTimeoutAwaitingDispatch(orderId);

        var stream = PmStream(orderId);
        harness.Dispatched.Where(d => d.Command is ReleaseInventory).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:release:{line:N}");
        harness.Dispatched.Where(d => d.Command is VoidPayment).Should().ContainSingle();
        harness.Dispatched.Where(d => d.Command is CancelOrder).Should().ContainSingle();
        (await harness.LoadPm(orderId))!.State.Should().Be(OrderFulfillmentState.Cancelled);
    }

    [Fact]
    public async Task PaymentAuthorized_redelivery_cancels_the_payment_timeout_on_every_delivery()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        harness.MapSku("SKU-1", Guid.NewGuid());
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-1", 1)));

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now));
        var auth = new PaymentAuthorized(Guid.NewGuid(), orderId, Usd(30m), "card", Now);
        await harness.Receive(auth);
        await harness.Receive(auth);

        // Cancel is unconditional on every delivery (Pattern C), so it fires twice.
        harness.DelayQueue.Cancelled.Where(c => c.Step == "await-payment-timeout").Should().HaveCount(2);
    }

    [Fact]
    public async Task OrderPlaced_redelivery_schedules_the_payment_timeout_exactly_once()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        await harness.SeedOrder(BuildOrder(orderId, (Guid.NewGuid(), "SKU-1", 1)));

        var placed = new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now);
        await harness.Receive(placed);
        await harness.Receive(placed);

        // Schedule is inside the NotStarted guard (Pattern S), so it fires only once.
        harness.DelayQueue.Scheduled.Where(s => s.Step == "await-payment-timeout").Should().ContainSingle();
    }

    [Fact]
    public async Task OrderPlaced_under_a_non_default_tenant_saves_the_pm_to_the_tenant_form_stream()
    {
        var harness = new OrderFulfillmentTestHarness();
        var orderId = Guid.NewGuid();
        var tenant = TenantId.From(Guid.NewGuid());

        await harness.Receive(new OrderPlaced(orderId, Guid.NewGuid(), Usd(30m), Now), MetadataForTenant(tenant));

        // Design A: the handler threads the inbound event's tenant into OrderFulfillmentStreams.For, so the
        // started PM lands at the tenant-form stream and not the default-form one. RED today: For ignores
        // the tenant and composes Default, so the tenant-form read finds nothing and the PM sits at the
        // default stream.
        var tenantFormPm = await harness.LoadPmForTenant(tenant, orderId);
        tenantFormPm.Should().NotBeNull();
        tenantFormPm!.State.Should().Be(OrderFulfillmentState.AwaitingPayment);
        (await harness.LoadPm(orderId)).Should().BeNull();
    }

    private static Order BuildOrder(Guid orderId, params (Guid LineId, string Sku, int Qty)[] lines)
    {
        var order = Order.Draft(orderId, Guid.NewGuid(), Now, "web");
        foreach (var (lineId, sku, qty) in lines)
        {
            order.AddLine(lineId, sku, qty, Usd(10m), Now);
        }
        order.SetShippingAddress(Destination, Now);
        order.Place(Now);
        return order;
    }

    private static StreamId PmStream(Guid orderId) =>
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, orderId);

    private static Money Usd(decimal amount) => new(amount, Currency.USD);

    private static EventMetadata MetadataForTenant(TenantId tenant) => new(
        EventId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        CausationId: Guid.NewGuid(),
        ActorId: Guid.NewGuid(),
        Source: "test",
        OccurredUtc: Now,
        Tenant: tenant);
}
