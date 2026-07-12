using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public class ProcessManagerRepositoryTests
{
    private static readonly StreamId Stream =
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, Guid.NewGuid());

    // The event that caused the process manager to transition. In production it is the event the
    // outbox dispatched to the handler, or the one that scheduled a timeout the delay queue
    // resurfaced; either way a save always has one behind it.
    private static readonly EventMetadata Causing = new(
        EventId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        CausationId: Guid.NewGuid(),
        ActorId: Guid.NewGuid(),
        Source: "test",
        SchemaVersion: 1,
        OccurredUtc: new DateTime(2026, 7, 12, 9, 0, 0, DateTimeKind.Utc),
        Tenant: WellKnownTenants.Default);

    // The context the outbox dispatcher establishes for a process-manager handler (ADR 0042). Every
    // production save runs under one, so the repository's other behaviors are exercised under one
    // too; SaveAsync_throws_when_no_command_context_is_in_flight covers its absence.
    private static ProcessManagerRepository<TestPm> NewRepository(IEventStore store)
        => new(
            store,
            new TestKit.StubCommandContextAccessor
            {
                Current = new CausedCommandContext(
                    Causing, SystemActors.OrderFulfillment, TimeProvider.System)
            },
            new TestKit.StubTenantAccessor { Current = WellKnownTenants.Default });

    private static TestPm Factory(StreamId sid) => new(sid);

    [Fact]
    public async Task LoadAsync_returns_null_for_an_empty_stream()
    {
        var repo = NewRepository(new InMemoryEventStore());
        (await repo.LoadAsync(Stream, Factory, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips_state_and_version()
    {
        var store = new InMemoryEventStore();
        var repo = NewRepository(store);
        var pm = new TestPm(Stream);
        pm.Start("started");
        pm.Advance("advanced");
        await repo.SaveAsync(pm, CancellationToken.None);

        var loaded = await repo.LoadAsync(Stream, Factory, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.State.Should().Be("advanced");
        loaded.Version.Should().Be(2);
    }

    [Fact]
    public async Task SaveAsync_clears_the_uncommitted_buffer()
    {
        var store = new InMemoryEventStore();
        var repo = NewRepository(store);
        var pm = new TestPm(Stream);
        pm.Start("started");

        await repo.SaveAsync(pm, CancellationToken.None);

        pm.GetUncommittedEvents().Should().BeEmpty();
    }

    [Fact]
    public async Task LoadOrNewAsync_returns_a_fresh_instance_when_the_stream_is_empty()
    {
        var repo = NewRepository(new InMemoryEventStore());

        var pm = await repo.LoadOrNewAsync(Stream, Factory, CancellationToken.None);

        pm.StreamId.Should().Be(Stream);
        pm.Version.Should().Be(0);
    }

    [Fact]
    public async Task LoadOrNewAsync_returns_the_loaded_instance_when_the_stream_exists()
    {
        var store = new InMemoryEventStore();
        var repo = NewRepository(store);
        var seeded = new TestPm(Stream);
        seeded.Start("started");
        await repo.SaveAsync(seeded, CancellationToken.None);

        var pm = await repo.LoadOrNewAsync(Stream, Factory, CancellationToken.None);

        pm.Version.Should().Be(1);
        pm.State.Should().Be("started");
    }

    [Fact]
    public async Task SaveAsync_raises_ConcurrencyException_on_a_stale_version()
    {
        var store = new InMemoryEventStore();
        var repo = NewRepository(store);

        // Two PMs at the same stream both think they start from version 0.
        var first = new TestPm(Stream);
        first.Start("first");
        await repo.SaveAsync(first, CancellationToken.None);

        var second = new TestPm(Stream);
        second.Start("second");
        var act = async () => await repo.SaveAsync(second, CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyException>();
    }

    // Fail-closed (ADR 0042's Revisit-when). Every production route into a PM save now carries a
    // command context: the outbox route gets one from the dispatcher, the timeout route from the
    // command pipeline. A save that finds none is a dispatch-wiring regression, and stamping empty
    // correlation, causation, and actor onto the PM's events would hide it behind rows that look
    // written. The repository throws instead, the posture the tenant guard in the same method
    // already takes. The tenant is set here so the assertion isolates the missing command context.
    [Fact]
    public async Task SaveAsync_throws_when_no_command_context_is_in_flight()
    {
        var repo = new ProcessManagerRepository<TestPm>(
            new InMemoryEventStore(),
            new TestKit.StubCommandContextAccessor { Current = null },
            new TestKit.StubTenantAccessor { Current = WellKnownTenants.Default });
        var pm = new TestPm(Stream);
        pm.Start("started");

        var act = async () => await repo.SaveAsync(pm, CancellationToken.None);

        await act.Should().ThrowAsync<MissingCommandContextException>();
    }

    private sealed record Started(string Note) : IProcessManagerEvent;
    private sealed record Advanced(string Note) : IProcessManagerEvent;

    private sealed class TestPm : ProcessManager
    {
        public TestPm(StreamId streamId) : base(streamId) { }

        public string? State { get; private set; }

        public void Start(string note) => RecordTransition(new Started(note));
        public void Advance(string note) => RecordTransition(new Advanced(note));

        protected override void Apply(IProcessManagerEvent @event)
        {
            State = @event switch
            {
                Started s => s.Note,
                Advanced a => a.Note,
                _ => State
            };
        }
    }
}
