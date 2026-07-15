using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.EventStore.ContractTests;

// The held-writer capability probes (docs/TDD_RULES.md §5, ADR 0044). These are the facts that
// reach past IEventStore into an interactive, held-open append to prove commit-order visibility
// and permanent gaps directly, rather than inferring them. An engine runs them only if its store
// can park an append mid-transaction; that is why the backend seam here is
// IHeldWriterContractBackend rather than the universal one. A backend whose held writer is a no-op
// would make these vacuous, so an engine that cannot supply a real one does not derive this suite.
public abstract class HeldWriterEventStoreContractTests
{
    // Long enough for an unserialized append to finish against a local backend. No fact asserts
    // on what completes inside it; the window only gives a racing append room to land if the
    // engine lets it, which is what a violation would look like.
    private static readonly TimeSpan RacingAppendWindow = TimeSpan.FromSeconds(2);

    // One backend per fact, isolated from every other. The adapter's test project supplies it.
    protected abstract Task<IHeldWriterContractBackend> CreateBackendAsync();

    [Fact]
    public async Task An_aggregate_append_racing_a_held_writer_surfaces_in_commit_order()
    {
        await using var backend = await CreateBackendAsync();
        await using var held = await backend.StartHeldWriterAsync(ContractEnvelopes.NewStreamId());

        var stream = ContractEnvelopes.NewStreamId();
        var racing = backend.Store.AppendAsync(stream, 0,
            [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
            CancellationToken.None);
        await Task.WhenAny(racing, Task.Delay(RacingAppendWindow));

        // Both streams are aggregate streams, so ReadAllAsync sees exactly what a projection
        // driving off it would see. This is the reader that would do the skipping.
        var observedBeforeCommit = await ReadFeedPositionsAsync(backend.Store);

        await held.CommitAsync();
        await racing;

        var observedAfterCommit = await ReadFeedPositionsAsync(backend.Store);

        AssertSurfacedAboveTheHighWaterMark(observedBeforeCommit, observedAfterCommit);
    }

    [Fact]
    public async Task A_process_manager_append_racing_a_held_writer_surfaces_in_commit_order()
    {
        await using var backend = await CreateBackendAsync();
        await using var held = await backend.StartHeldWriterAsync(ContractEnvelopes.NewStreamId());

        var pmStream = ContractEnvelopes.NewProcessManagerStreamId();
        var racing = backend.Store.AppendProcessManagerEventsAsync(pmStream, 0,
            [ContractEnvelopes.BuildProcessManager(pmStream, 1, new ContractStepRecorded(1))],
            CancellationToken.None);
        await Task.WhenAny(racing, Task.Delay(RacingAppendWindow));

        // ReadAllAsync excludes PM streams (ADR 0013), so it is structurally blind to this
        // racing append and would make the probe vacuous. The position sequence spans both
        // append paths, so this fact reads committed positions through the backend hook.
        var observedBeforeCommit = await backend.ReadCommittedPositionsAsync();

        await held.CommitAsync();
        await racing;

        var observedAfterCommit = await backend.ReadCommittedPositionsAsync();

        AssertSurfacedAboveTheHighWaterMark(observedBeforeCommit, observedAfterCommit);
    }

    [Fact]
    public async Task A_rolled_back_append_burns_positions_and_readers_proceed_past_the_gap()
    {
        await using var backend = await CreateBackendAsync();
        var before = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(before, 0,
            [ContractEnvelopes.Build(before, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
            CancellationToken.None);

        // The held writer draws a position and then throws it away. Position assignment does not
        // roll back with the transaction, so that position is burned for good.
        await using var held =
            await backend.StartHeldWriterAsync(ContractEnvelopes.NewStreamId());
        await held.RollbackAsync();

        var after = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(after, 0,
            [ContractEnvelopes.Build(after, 1, new ContractOrderNoted("after the gap"))],
            CancellationToken.None);

        var read = await ReadFeedPositionsAsync(backend.Store);

        // Monotonic ascending, and deliberately not contiguous. The reader walks straight past
        // the burned position rather than waiting for it to fill in, because under ADR 0044 a
        // gap a committed reader observes is permanent and never transient.
        read.Should().HaveCount(2).And.BeInAscendingOrder();
        (read[1] - read[0]).Should().BeGreaterThan(
            1, "the rolled-back append burned at least one position between the two that landed");
    }

    // The commit-ordering invariant, and nothing else: every position that surfaces once the
    // held writer commits must sit above everything an earlier committed read already observed.
    // A position landing at or below that mark is one a checkpointing reader has already read
    // past and will never come back for, which is silent, permanent event loss.
    private static void AssertSurfacedAboveTheHighWaterMark(
        IReadOnlyList<long> observedBeforeCommit, IReadOnlyList<long> observedAfterCommit)
    {
        // Positions start above zero, so zero stands for "nothing observed yet".
        var highWater = observedBeforeCommit.DefaultIfEmpty(0L).Max();
        var surfacedLate = observedAfterCommit.Except(observedBeforeCommit).Order().ToArray();

        surfacedLate.Should().OnlyContain(
            position => position > highWater,
            "a position becoming visible at or below the high-water mark {0} a reader had "
            + "already observed is one it has checkpointed past and will never read. Observed "
            + "before the held writer committed: [{1}]. Surfaced after it committed: [{2}].",
            highWater,
            string.Join(", ", observedBeforeCommit),
            string.Join(", ", surfacedLate));

        // Both writers land. Serializing the append must not lose the racing one.
        observedAfterCommit.Should().HaveCount(2).And.BeInAscendingOrder();
    }

    private static async Task<IReadOnlyList<long>> ReadFeedPositionsAsync(IEventStore store)
    {
        var feed = await ReadFeedAsync(store);
        return feed.Select(e => e.GlobalPosition).ToArray();
    }

    private static async Task<IReadOnlyList<EventEnvelope>> ReadFeedAsync(
        IEventStore store, long fromPosition = 0)
    {
        var feed = new List<EventEnvelope>();
        await foreach (var envelope in store.ReadAllAsync(fromPosition, CancellationToken.None))
        {
            feed.Add(envelope);
        }
        return feed;
    }
}
