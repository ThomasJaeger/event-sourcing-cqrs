using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.ProcessManagers.Returns;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.ProcessManagers.Tests;

// Handler-level coverage for ReturnProcessManagerHandler: the happy path through
// restock and void, and the stuck-state triggers (unmapped SKU, missing payment
// mapping, void failure). Stuck is terminal with no compensation (Decision 12); a
// void failure after a successful restock leaves the restock in place, the
// operational-realism gap the PM accepts for v1.
public sealed class ReturnProcessManagerHandlerTests
{
    private static readonly DateTime Now = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Address Destination = new("1 Main St", "Smalltown", "12345", "US");

    [Fact]
    public async Task Happy_path_restocks_every_line_voids_and_completes()
    {
        var harness = new ReturnProcessManagerTestHarness();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var lineA = Guid.NewGuid();
        var lineB = Guid.NewGuid();
        harness.MapSku("SKU-A", Guid.NewGuid());
        harness.MapSku("SKU-B", Guid.NewGuid());
        harness.MapPayment(orderId, Guid.NewGuid());
        await harness.SeedShipment(Shipment.Schedule(
            shipmentId, orderId, Destination,
            [new ShipmentLine(orderId, lineA, "SKU-A", 2), new ShipmentLine(orderId, lineB, "SKU-B", 3)],
            Now));

        await harness.Receive(new ShipmentReturned(shipmentId, "Customer return", Now));

        var stream = StreamId.ForProcessManager(StreamPrefixes.ReturnPm, orderId);
        harness.Dispatched.Where(d => d.Command is AdjustInventory).Select(d => d.IdempotencyKey)
            .Should().BeEquivalentTo(new[]
            {
                $"{stream.Value}:restock:{lineA:N}",
                $"{stream.Value}:restock:{lineB:N}"
            });
        harness.Dispatched.Where(d => d.Command is VoidPayment).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"{stream.Value}:void-payment");
        (await harness.LoadPm(orderId))!.State.Should().Be(ReturnState.Completed);
    }

    [Fact]
    public async Task Unmapped_sku_marks_the_pm_stuck_without_voiding()
    {
        var harness = new ReturnProcessManagerTestHarness();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        harness.MapPayment(orderId, Guid.NewGuid());
        // SKU-MISSING is unmapped, so its restock fails.
        await harness.SeedShipment(Shipment.Schedule(
            shipmentId, orderId, Destination,
            [new ShipmentLine(orderId, Guid.NewGuid(), "SKU-MISSING", 1)], Now));

        await harness.Receive(new ShipmentReturned(shipmentId, "Customer return", Now));

        harness.Dispatched.Should().NotContain(d => d.Command is VoidPayment);
        var pm = await harness.LoadPm(orderId);
        pm!.State.Should().Be(ReturnState.Stuck);
        pm.StuckReason.Should().Contain("no inventory mapping");
    }

    [Fact]
    public async Task Void_failure_marks_the_pm_stuck_after_a_successful_restock()
    {
        var harness = new ReturnProcessManagerTestHarness();
        harness.Bus.OutcomeFor = cmd => cmd is VoidPayment
            ? CommandOutcome.Failed(new DomainException("payment already reversed"))
            : CommandOutcome.Success();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        harness.MapSku("SKU-A", Guid.NewGuid());
        harness.MapPayment(orderId, Guid.NewGuid());
        await harness.SeedShipment(Shipment.Schedule(
            shipmentId, orderId, Destination,
            [new ShipmentLine(orderId, Guid.NewGuid(), "SKU-A", 1)], Now));

        await harness.Receive(new ShipmentReturned(shipmentId, "Customer return", Now));

        // The restock happened (and is not reversed); the void failed -> Stuck.
        harness.Dispatched.Where(d => d.Command is AdjustInventory).Should().ContainSingle();
        harness.Dispatched.Where(d => d.Command is VoidPayment).Should().ContainSingle();
        var pm = await harness.LoadPm(orderId);
        pm!.State.Should().Be(ReturnState.Stuck);
        pm.StuckReason.Should().Contain("Void payment failed");
    }

    [Fact]
    public async Task Missing_payment_mapping_marks_the_pm_stuck_after_restock()
    {
        var harness = new ReturnProcessManagerTestHarness();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        harness.MapSku("SKU-A", Guid.NewGuid());
        // No MapPayment: the payment lookup returns null.
        await harness.SeedShipment(Shipment.Schedule(
            shipmentId, orderId, Destination,
            [new ShipmentLine(orderId, Guid.NewGuid(), "SKU-A", 1)], Now));

        await harness.Receive(new ShipmentReturned(shipmentId, "Customer return", Now));

        harness.Dispatched.Where(d => d.Command is AdjustInventory).Should().ContainSingle();
        harness.Dispatched.Should().NotContain(d => d.Command is VoidPayment);
        var pm = await harness.LoadPm(orderId);
        pm!.State.Should().Be(ReturnState.Stuck);
        pm.StuckReason.Should().Contain("No payment mapping");
    }

    [Fact]
    public async Task Redelivery_after_completion_is_a_no_op()
    {
        var harness = new ReturnProcessManagerTestHarness();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        harness.MapSku("SKU-A", Guid.NewGuid());
        harness.MapPayment(orderId, Guid.NewGuid());
        await harness.SeedShipment(Shipment.Schedule(
            shipmentId, orderId, Destination,
            [new ShipmentLine(orderId, Guid.NewGuid(), "SKU-A", 1)], Now));

        var returned = new ShipmentReturned(shipmentId, "Customer return", Now);
        await harness.Receive(returned);
        var versionAfterFirst = (await harness.LoadPm(orderId))!.Version;
        await harness.Receive(returned);
        var pm = await harness.LoadPm(orderId);

        // Completed is terminal: the second delivery guards out, no re-dispatch.
        pm!.State.Should().Be(ReturnState.Completed);
        pm.Version.Should().Be(versionAfterFirst);
        harness.Dispatched.Where(d => d.Command is AdjustInventory).Should().ContainSingle();
        harness.Dispatched.Where(d => d.Command is VoidPayment).Should().ContainSingle();
    }
}
