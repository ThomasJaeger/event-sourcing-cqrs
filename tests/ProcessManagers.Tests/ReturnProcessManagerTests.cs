using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.ProcessManagers.Returns;
using EventSourcingCqrs.ProcessManagers.Returns.Events;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.ProcessManagers.Tests;

// Apply-and-rehydrate coverage for the Return PM scaffolding. One test per event
// confirms Apply mutates state and Version increments; a rehydration test confirms
// LoadFromHistory rebuilds the same state; a never-started test pins the explicit
// NotStarted zero; a default-branch test pins Apply's rejection of an unknown
// event. No handler involved; transitions are driven directly on the PM.
public sealed class ReturnProcessManagerTests
{
    private static StreamId NewStream() =>
        StreamId.ForProcessManager(StreamPrefixes.ReturnPm, Guid.NewGuid());

    private static ReturnProcessManager NewPm() => new(NewStream());

    [Fact]
    public void A_freshly_constructed_pm_is_not_started()
    {
        var pm = NewPm();

        pm.State.Should().Be(ReturnState.NotStarted);
        pm.Version.Should().Be(0);
        pm.GetUncommittedEvents().Should().BeEmpty();
    }

    [Fact]
    public void Start_records_the_order_and_enters_restocking()
    {
        var pm = NewPm();
        var orderId = Guid.NewGuid();

        pm.Start(orderId);

        pm.State.Should().Be(ReturnState.RestockingInventory);
        pm.OrderId.Should().Be(orderId);
        pm.Version.Should().Be(1);
        pm.GetUncommittedEvents().Should().ContainSingle()
            .Which.Should().BeOfType<ReturnProcessingStarted>();
    }

    [Fact]
    public void RecordRestock_enters_voiding_payment()
    {
        var pm = Started();

        pm.RecordRestock();

        pm.State.Should().Be(ReturnState.VoidingPayment);
        pm.GetUncommittedEvents().Last().Should().BeOfType<InventoryRestockRecorded>();
    }

    [Fact]
    public void RecordVoid_holds_state_until_completion()
    {
        var pm = ThroughRestock();
        var before = pm.Version;

        pm.RecordVoid();

        pm.State.Should().Be(ReturnState.VoidingPayment);
        pm.Version.Should().Be(before + 1);
        pm.GetUncommittedEvents().Last().Should().BeOfType<PaymentVoidRecorded>();
    }

    [Fact]
    public void Complete_reaches_the_completed_terminal()
    {
        var pm = ThroughRestock();
        pm.RecordVoid();

        pm.Complete();

        pm.State.Should().Be(ReturnState.Completed);
        pm.GetUncommittedEvents().Last().Should().BeOfType<ReturnProcessingCompleted>();
    }

    [Fact]
    public void MarkStuck_records_the_reason_and_reaches_the_stuck_terminal()
    {
        var pm = Started();

        pm.MarkStuck("restock failed for line X");

        pm.State.Should().Be(ReturnState.Stuck);
        pm.StuckReason.Should().Be("restock failed for line X");
        pm.GetUncommittedEvents().Last().Should().BeOfType<ReturnProcessingStuck>();
    }

    [Fact]
    public void LoadFromHistory_rebuilds_the_same_state_as_the_recorded_transitions()
    {
        var driven = ThroughRestock();
        driven.RecordVoid();
        driven.Complete();
        var history = driven.GetUncommittedEvents().ToList();

        var rehydrated = NewPm();
        rehydrated.LoadFromHistory(history);

        rehydrated.State.Should().Be(driven.State);
        rehydrated.Version.Should().Be(driven.Version);
        rehydrated.OrderId.Should().Be(driven.OrderId);
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

    private static ReturnProcessManager Started()
    {
        var pm = NewPm();
        pm.Start(Guid.NewGuid());
        return pm;
    }

    private static ReturnProcessManager ThroughRestock()
    {
        var pm = Started();
        pm.RecordRestock();
        return pm;
    }

    private sealed record UnknownPmEvent : IProcessManagerEvent;
}
