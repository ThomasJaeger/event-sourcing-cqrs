using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.ProcessManagers.Tests;

// Apply-and-rehydrate coverage for the OrderFulfillment PM scaffolding (commit
// 20). One test per PM event type confirms Apply mutates state and Version
// increments; a rehydration test confirms LoadFromHistory rebuilds the same state
// as the recorded transitions; a never-started test pins the explicit NotStarted
// zero; a default-branch test pins Apply's rejection of an unknown event. No
// handler exists yet, so transitions are driven directly on the PM.
public sealed class OrderFulfillmentProcessManagerTests
{
    private static readonly Money Total = new(149.99m, Currency.USD);

    private static StreamId NewStream() =>
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, Guid.NewGuid());

    private static OrderFulfillmentProcessManager NewPm() => new(NewStream());

    [Fact]
    public void A_freshly_constructed_pm_is_not_started()
    {
        var pm = NewPm();

        pm.State.Should().Be(OrderFulfillmentState.NotStarted);
        pm.Version.Should().Be(0);
        pm.GetUncommittedEvents().Should().BeEmpty();
    }

    [Fact]
    public void Start_records_the_seed_and_awaits_payment()
    {
        var pm = NewPm();
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        pm.Start(orderId, Total, paymentId);

        pm.State.Should().Be(OrderFulfillmentState.AwaitingPayment);
        pm.OrderId.Should().Be(orderId);
        pm.PaymentId.Should().Be(paymentId);
        pm.OrderTotal.Should().Be(Total);
        pm.Version.Should().Be(1);
        pm.GetUncommittedEvents().Should().ContainSingle()
            .Which.Should().BeOfType<OrderFulfillmentStarted>();
    }

    [Fact]
    public void RecordPaymentAuthorized_moves_to_awaiting_inventory()
    {
        var pm = Started();
        var before = pm.Version;

        pm.RecordPaymentAuthorized();

        pm.State.Should().Be(OrderFulfillmentState.AwaitingInventory);
        pm.Version.Should().Be(before + 1);
        pm.GetUncommittedEvents().Last().Should().BeOfType<PaymentAuthorizationRecorded>();
    }

    [Fact]
    public void RecordLineReserved_tracks_the_line_as_reserved_without_changing_state()
    {
        var pm = ThroughPayment();
        var lineId = Guid.NewGuid();
        var inventoryId = Guid.NewGuid();
        var before = pm.Version;

        pm.RecordLineReserved(lineId, "SKU-1", 3, inventoryId);

        pm.State.Should().Be(OrderFulfillmentState.AwaitingInventory);
        pm.Version.Should().Be(before + 1);
        var line = pm.Reservations[lineId];
        line.Status.Should().Be(ReservationLineStatus.Reserved);
        line.InventoryId.Should().Be(inventoryId);
        line.Sku.Should().Be("SKU-1");
        line.Quantity.Should().Be(3);
        line.FailureReason.Should().BeNull();
        pm.GetUncommittedEvents().Last().Should().BeOfType<ReservationSucceeded>();
    }

    [Fact]
    public void RecordLineReservationFailed_tracks_the_line_as_failed()
    {
        var pm = ThroughPayment();
        var lineId = Guid.NewGuid();

        pm.RecordLineReservationFailed(lineId, "SKU-2", 1, "out of stock");

        var line = pm.Reservations[lineId];
        line.Status.Should().Be(ReservationLineStatus.Failed);
        line.InventoryId.Should().BeNull();
        line.FailureReason.Should().Be("out of stock");
        pm.GetUncommittedEvents().Last().Should().BeOfType<ReservationFailed>();
    }

    [Fact]
    public void CompleteReservations_moves_to_awaiting_shipment()
    {
        var pm = ThroughPayment();
        pm.RecordLineReserved(Guid.NewGuid(), "SKU-1", 1, Guid.NewGuid());

        pm.CompleteReservations();

        pm.State.Should().Be(OrderFulfillmentState.AwaitingShipment);
        pm.GetUncommittedEvents().Last().Should().BeOfType<InventoryReservationsCompleted>();
    }

    [Fact]
    public void RequestShipmentScheduling_stores_the_shipment_id_and_awaits_dispatch()
    {
        var pm = ThroughReservations();
        var shipmentId = Guid.NewGuid();

        pm.RequestShipmentScheduling(shipmentId);

        pm.State.Should().Be(OrderFulfillmentState.AwaitingDispatch);
        pm.ShipmentId.Should().Be(shipmentId);
        pm.GetUncommittedEvents().Last().Should().BeOfType<ShipmentSchedulingRequested>();
    }

    [Fact]
    public void RecordShipmentDispatched_moves_to_awaiting_delivery()
    {
        var pm = ThroughScheduling();

        pm.RecordShipmentDispatched();

        pm.State.Should().Be(OrderFulfillmentState.AwaitingDelivery);
        pm.GetUncommittedEvents().Last().Should().BeOfType<ShipmentDispatchRecorded>();
    }

    [Fact]
    public void RecordShipmentDelivered_holds_state_until_completion()
    {
        var pm = ThroughDispatch();
        var before = pm.Version;

        pm.RecordShipmentDelivered();

        pm.State.Should().Be(OrderFulfillmentState.AwaitingDelivery);
        pm.Version.Should().Be(before + 1);
        pm.GetUncommittedEvents().Last().Should().BeOfType<ShipmentDeliveryRecorded>();
    }

    [Fact]
    public void StartCancellation_records_the_reason_without_changing_state()
    {
        var pm = ThroughPayment();
        var before = pm.State;

        pm.StartCancellation("all reservations failed");

        pm.State.Should().Be(before);
        pm.CancellationReason.Should().Be("all reservations failed");
        pm.GetUncommittedEvents().Last().Should().BeOfType<OrderFulfillmentCancellationStarted>();
    }

    [Fact]
    public void ReleaseReservation_flips_the_line_to_released_and_enters_releasing_inventory()
    {
        var pm = ThroughPayment();
        var lineId = Guid.NewGuid();
        pm.RecordLineReserved(lineId, "SKU-1", 2, Guid.NewGuid());
        pm.StartCancellation("shipment dispatch failed");

        pm.ReleaseReservation(lineId);

        pm.State.Should().Be(OrderFulfillmentState.ReleasingInventory);
        pm.Reservations[lineId].Status.Should().Be(ReservationLineStatus.Released);
        pm.GetUncommittedEvents().Last().Should().BeOfType<ReservationReleased>();
    }

    [Fact]
    public void RequestRefund_enters_refunding_payment()
    {
        var pm = ThroughPayment();
        pm.StartCancellation("shipment dispatch failed");

        pm.RequestRefund("compensating refund");

        pm.State.Should().Be(OrderFulfillmentState.RefundingPayment);
        pm.GetUncommittedEvents().Last().Should().BeOfType<RefundRequested>();
    }

    [Fact]
    public void OrderFulfillmentCompleted_reaches_the_outcome_terminal()
    {
        var happy = ThroughDelivery();
        happy.Complete();
        happy.State.Should().Be(OrderFulfillmentState.Completed);
        happy.GetUncommittedEvents().Last().Should().BeOfType<OrderFulfillmentCompleted>()
            .Which.Outcome.Should().Be(OrderFulfillmentOutcome.Completed);

        var compensated = ThroughPayment();
        compensated.StartCancellation("all reservations failed");
        compensated.RequestRefund("compensating refund");
        compensated.CompleteAsCancelled();
        compensated.State.Should().Be(OrderFulfillmentState.Cancelled);
        compensated.GetUncommittedEvents().Last().Should().BeOfType<OrderFulfillmentCompleted>()
            .Which.Outcome.Should().Be(OrderFulfillmentOutcome.Cancelled);
    }

    [Fact]
    public void LoadFromHistory_rebuilds_the_same_state_as_the_recorded_transitions()
    {
        var driven = ThroughDelivery();
        driven.Complete();
        var history = driven.GetUncommittedEvents().ToList();

        var rehydrated = NewPm();
        rehydrated.LoadFromHistory(history);

        rehydrated.State.Should().Be(driven.State);
        rehydrated.Version.Should().Be(driven.Version);
        rehydrated.OrderId.Should().Be(driven.OrderId);
        rehydrated.PaymentId.Should().Be(driven.PaymentId);
        rehydrated.ShipmentId.Should().Be(driven.ShipmentId);
        // LoadFromHistory replays without buffering, unlike RecordTransition.
        rehydrated.GetUncommittedEvents().Should().BeEmpty();
    }

    [Fact]
    public void Apply_rejects_an_unknown_process_manager_event()
    {
        var pm = NewPm();

        var act = () => pm.LoadFromHistory([new UnknownPmEvent()]);

        act.Should().Throw<InvalidOperationException>();
    }

    private static OrderFulfillmentProcessManager Started()
    {
        var pm = NewPm();
        pm.Start(Guid.NewGuid(), Total, Guid.NewGuid());
        return pm;
    }

    private static OrderFulfillmentProcessManager ThroughPayment()
    {
        var pm = Started();
        pm.RecordPaymentAuthorized();
        return pm;
    }

    private static OrderFulfillmentProcessManager ThroughReservations()
    {
        var pm = ThroughPayment();
        pm.RecordLineReserved(Guid.NewGuid(), "SKU-1", 1, Guid.NewGuid());
        pm.CompleteReservations();
        return pm;
    }

    private static OrderFulfillmentProcessManager ThroughScheduling()
    {
        var pm = ThroughReservations();
        pm.RequestShipmentScheduling(Guid.NewGuid());
        return pm;
    }

    private static OrderFulfillmentProcessManager ThroughDispatch()
    {
        var pm = ThroughScheduling();
        pm.RecordShipmentDispatched();
        return pm;
    }

    private static OrderFulfillmentProcessManager ThroughDelivery()
    {
        var pm = ThroughDispatch();
        pm.RecordShipmentDelivered();
        return pm;
    }

    private sealed record UnknownPmEvent : IProcessManagerEvent;
}
