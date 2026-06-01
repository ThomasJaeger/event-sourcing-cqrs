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

    private static ProcessManagerRepository<TestPm> NewRepository(IEventStore store)
        => new(store, new AsyncLocalCommandContextAccessor());

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
