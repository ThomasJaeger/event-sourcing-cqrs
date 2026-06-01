using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests;

public class ProcessManagerTests
{
    private static readonly StreamId Stream =
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, Guid.NewGuid());

    [Fact]
    public void RecordTransition_applies_enqueues_and_increments_version()
    {
        var pm = new TestProcessManager(Stream);
        pm.Start("started");

        pm.Version.Should().Be(1);
        pm.State.Should().Be("started");
        pm.GetUncommittedEvents().Should().ContainSingle();
    }

    [Fact]
    public void LoadFromHistory_applies_and_increments_without_enqueueing()
    {
        var pm = new TestProcessManager(Stream);
        pm.LoadFromHistory([new TestTransition("a"), new TestTransition("b")]);

        pm.Version.Should().Be(2);
        pm.State.Should().Be("b");
        pm.GetUncommittedEvents().Should().BeEmpty();
    }

    [Fact]
    public void MarkCommitted_clears_the_uncommitted_buffer()
    {
        var pm = new TestProcessManager(Stream);
        pm.Start("started");

        pm.MarkCommitted();

        pm.GetUncommittedEvents().Should().BeEmpty();
    }

    [Fact]
    public void GetUncommittedEvents_returns_without_clearing()
    {
        var pm = new TestProcessManager(Stream);
        pm.Start("one");
        pm.Advance("two");

        pm.GetUncommittedEvents().Should().HaveCount(2);
        // Reading does not clear; only MarkCommitted does (the deliberate split).
        pm.GetUncommittedEvents().Should().HaveCount(2);
    }

    [Fact]
    public void StreamId_is_the_value_supplied_at_construction()
    {
        new TestProcessManager(Stream).StreamId.Should().Be(Stream);
    }

    [Fact]
    public void Record_then_load_reach_the_same_state_and_version()
    {
        var recorded = new TestProcessManager(Stream);
        recorded.Start("one");
        recorded.Advance("two");

        var rehydrated = new TestProcessManager(Stream);
        rehydrated.LoadFromHistory(recorded.GetUncommittedEvents());

        rehydrated.State.Should().Be(recorded.State);
        rehydrated.Version.Should().Be(recorded.Version);
    }

    private sealed record TestTransition(string Note) : IProcessManagerEvent;

    private sealed class TestProcessManager : ProcessManager
    {
        public TestProcessManager(StreamId streamId) : base(streamId) { }

        public string? State { get; private set; }

        public void Start(string note) => RecordTransition(new TestTransition(note));
        public void Advance(string note) => RecordTransition(new TestTransition(note));

        protected override void Apply(IProcessManagerEvent @event)
        {
            if (@event is TestTransition t)
            {
                State = t.Note;
            }
        }
    }
}
