using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.EventStore.ContractTests;

// The one IEventStore contract suite (docs/TDD_RULES.md §5). Every adapter subclasses it,
// supplies a backend, and passes the identical facts. PLAN.md's v1 completion criterion is that
// all four adapters pass this suite; the suite is what makes that claim checkable instead of
// merely asserted.
//
// Provenance, stated plainly rather than dressed up. Against the PostgreSQL adapter every fact
// here is a characterization: the behaviors are already provided by production code, most are
// already pinned Postgres-homed, and these facts were green the day they were written. No RED
// preceded them and none was staged. The suite's honest failure window opens against the next
// adapter, which has to earn the same green without the suite bending to accommodate it.
//
// This project references Domain.Abstractions and the test frameworks, and no adapter. That is
// the enforcement: the suite cannot reach for an engine's types even by accident, so what it
// pins is what the port owes rather than what one implementation happens to do.
//
// The suite never asserts on a timestamp and never orders by one. Global position is the only
// ordering the contract has.
public abstract class EventStoreContractTests
{
    // Long enough for an unserialized append to finish against a local backend. No fact asserts
    // on what completes inside it; the window only gives a racing append room to land if the
    // engine lets it, which is what a violation would look like.
    private static readonly TimeSpan RacingAppendWindow = TimeSpan.FromSeconds(2);

    // One backend per fact, isolated from every other. The adapter's test project supplies it.
    protected abstract Task<IEventStoreContractBackend> CreateBackendAsync();

    [Fact]
    public async Task Append_then_read_returns_the_same_events_in_order_from_version_zero()
    {
        await using var backend = await CreateBackendAsync();
        var stream = ContractEnvelopes.NewStreamId();
        var appended = new[]
        {
            ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m)),
            ContractEnvelopes.Build(stream, 2, new ContractOrderNoted("second")),
            ContractEnvelopes.Build(stream, 3, new ContractOrderNoted("third")),
        };

        await backend.Store.AppendAsync(stream, 0, appended, CancellationToken.None);

        var read = await backend.Store.ReadStreamAsync(stream, 0, CancellationToken.None);

        read.Select(e => e.StreamVersion).Should().Equal(1, 2, 3);
        read.Select(e => e.EventId).Should().Equal(appended.Select(e => e.EventId));
        read.Select(e => e.Payload).Should().Equal(appended.Select(e => e.Payload));
        read.Select(e => e.GlobalPosition).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Reading_a_stream_from_an_arbitrary_version_returns_the_tail()
    {
        await using var backend = await CreateBackendAsync();
        var stream = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(stream, 0,
            [
                ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m)),
                ContractEnvelopes.Build(stream, 2, new ContractOrderNoted("second")),
                ContractEnvelopes.Build(stream, 3, new ContractOrderNoted("third")),
            ],
            CancellationToken.None);

        var tail = await backend.Store.ReadStreamAsync(stream, 1, CancellationToken.None);

        // fromVersion is exclusive: version 1 is skipped, 2 and 3 remain.
        tail.Select(e => e.StreamVersion).Should().Equal(2, 3);
    }

    [Fact]
    public async Task Appending_at_a_stale_expected_version_raises_the_concurrency_contract()
    {
        await using var backend = await CreateBackendAsync();
        var stream = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(stream, 0,
            [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
            CancellationToken.None);

        // A second writer still believes the stream sits at version 0, so it computes version 1
        // for its first envelope. Conflict detection is let-the-write-fail: the append is
        // attempted, the engine's uniqueness constraint on (stream, version) fires, and the
        // adapter translates its native error into the one store-agnostic type. No read-check,
        // no pre-flight compare.
        var stale = ContractEnvelopes.Build(stream, 1, new ContractOrderNoted("stale"));
        var act = async () =>
            await backend.Store.AppendAsync(stream, 0, [stale], CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ConcurrencyException>()).Which;
        ex.StreamId.Should().Be(stream);
        ex.ExpectedVersion.Should().Be(0);
    }

    [Fact]
    public async Task Appending_a_duplicate_event_id_propagates_untranslated()
    {
        await using var backend = await CreateBackendAsync();
        var streamA = ContractEnvelopes.NewStreamId();
        var streamB = ContractEnvelopes.NewStreamId();
        var sharedEventId = Guid.NewGuid();

        await backend.Store.AppendAsync(streamA, 0,
            [
                ContractEnvelopes.Build(
                    streamA, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m), sharedEventId),
            ],
            CancellationToken.None);

        // Append is not idempotent by event id and does not dedupe. A reused event id is a
        // programming bug, not a retry-and-replay condition, so it must not arrive dressed as
        // ConcurrencyException and must not be silently swallowed. The concrete exception type
        // is engine-owned, so the suite pins the strongest thing every engine owes: it throws,
        // it is not the concurrency contract, and the append did not land.
        var collision = ContractEnvelopes.Build(
            streamB, 1, new ContractOrderNoted("collision"), sharedEventId);
        var act = async () =>
            await backend.Store.AppendAsync(streamB, 0, [collision], CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.Should().NotBeOfType<ConcurrencyException>();

        (await backend.Store.ReadStreamAsync(streamB, 0, CancellationToken.None))
            .Should().BeEmpty();
    }

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

    [Fact]
    public async Task Reading_a_non_existent_stream_returns_empty()
    {
        await using var backend = await CreateBackendAsync();

        var read = await backend.Store.ReadStreamAsync(
            ContractEnvelopes.NewStreamId(), 0, CancellationToken.None);

        read.Should().BeEmpty();
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
        var positions = new List<long>();
        await foreach (var envelope in store.ReadAllAsync(0, CancellationToken.None))
        {
            positions.Add(envelope.GlobalPosition);
        }
        return positions;
    }
}
