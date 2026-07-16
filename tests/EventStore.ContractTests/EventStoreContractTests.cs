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
//
// ReadAllForTenantAsync inherits ADR 0044's commit-order visibility exactly as ReadAllAsync
// does: it is the same feed with a tenant predicate and a ceiling, so an observed gap in its
// window is permanent and a high-water mark over its committed reads is safe. The pre-flight
// left that reader's safety classification split; the ADR settles it, and the facts below pin
// its bounds and its filter rather than leaving them to be inferred from the Postgres adapter.
public abstract class EventStoreContractTests
{
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
    public async Task A_duplicate_event_id_is_never_reported_as_a_concurrency_conflict()
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

        // A reused event id is a programming bug, not a retry-and-replay condition. Whether an
        // engine rejects it, dedupes it, or lets it through is its own to settle; the engines that
        // raise pin that stronger claim in the throwing-duplicate capability suite. The one thing
        // no engine may do is dress the collision up as the concurrency contract, because a handler
        // catching ConcurrencyException would retry a bug forever.
        var collision = ContractEnvelopes.Build(
            streamB, 1, new ContractOrderNoted("collision"), sharedEventId);

        var thrown = await Record.ExceptionAsync(
            () => backend.Store.AppendAsync(streamB, 0, [collision], CancellationToken.None));

        (thrown is null or not ConcurrencyException).Should().BeTrue(
            "a duplicate event id must never be reported as a concurrency conflict");
    }

    [Fact]
    public async Task Reading_a_non_existent_stream_returns_empty()
    {
        await using var backend = await CreateBackendAsync();

        var read = await backend.Store.ReadStreamAsync(
            ContractEnvelopes.NewStreamId(), 0, CancellationToken.None);

        read.Should().BeEmpty();
    }

    [Fact]
    public async Task Non_ascii_payload_and_metadata_round_trip_with_exact_string_equality()
    {
        await using var backend = await CreateBackendAsync();
        var stream = ContractEnvelopes.NewStreamId();

        // Accented Latin, CJK, and an emoji (a surrogate pair in UTF-16). The contract is that
        // what goes in comes back out, whatever the engine does to store it.
        //
        // What this fact does NOT catch, written down so nobody trusts it for more than it is
        // worth. System.Text.Json escapes non-ASCII to \uXXXX by default, so the JSON handed to
        // a driver is already pure ASCII and survives even a parameter binding that narrows to a
        // single-byte codepage. That is measured, not assumed: the SQL Server adapter passes
        // this fact with the corrupting binding deliberately put back in. Catching that bug
        // needs a byte-level assertion on the stored column, which is engine-specific and cannot
        // live in an engine-agnostic suite. The guard that does work is the adapter's own
        // parameter types.
        //
        // The fact still earns its keep. It pins the round-trip for any engine whose serializer
        // does not escape, and it goes red the day someone sets a relaxed JSON encoder and the
        // raw bytes start reaching the driver.
        const string NonAscii = "café / 日本語 / \U0001F680 / Ωμέγα";
        var payload = new ContractOrderNoted(NonAscii);

        // Payload and metadata are separate columns, so both are driven. Source is the metadata's
        // one free-text field and therefore the only way to put non-ASCII through that column.
        await backend.Store.AppendAsync(stream, 0,
            [ContractEnvelopes.Build(stream, 1, payload, source: NonAscii)],
            CancellationToken.None);

        var read = await backend.Store.ReadStreamAsync(stream, 0, CancellationToken.None);

        read.Should().HaveCount(1);
        read[0].Payload.Should().BeOfType<ContractOrderNoted>().Which.Note.Should().Be(NonAscii);
        read[0].Metadata.Source.Should().Be(NonAscii);
    }

    [Fact]
    public async Task Reading_a_process_manager_stream_with_a_non_pm_stream_id_fails_loudly()
    {
        await using var backend = await CreateBackendAsync();
        var aggregateStream = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(aggregateStream, 0,
            [ContractEnvelopes.Build(
                aggregateStream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
            CancellationToken.None);

        // The guard is on the shape of the stream id, not on whether the stream exists, so an
        // aggregate stream carrying real events must still be refused rather than handed back
        // through the PM read. PM payloads resolve through a separate registry (ADR 0013) and a
        // caller reaching for one with an aggregate id has a bug (ADR 0011).
        //
        // The suite pins the loudness and not the type. IEventStore's stated contract says
        // "failing loudly" and names no exception, so holding every engine to one type would be
        // the suite inventing a contract the port never made. The shipped stores happen to raise
        // ArgumentException.
        //
        // NotImplementedException is excluded, and that exclusion is the fact's teeth. Staying
        // type-agnostic means an unimplemented member satisfies the assertion by throwing on the
        // way to doing nothing, so an adapter part-way through a phase goes green here while the
        // guard does not exist. Phase 14 slice 1 hit exactly that window: a skeleton whose members
        // all threw NotImplementedException would have passed this fact the moment its append
        // turned green, before its PM read had a guard at all. The exclusion closes the window
        // without inventing the type contract the port declined to make.
        var act = async () => await backend.Store.ReadProcessManagerStreamAsync(
            aggregateStream, 0, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        thrown.Should().NotBeOfType<NotImplementedException>(
            "an unimplemented member is not a guard, however loudly it throws");
    }

    [Fact]
    public async Task Reading_all_excludes_process_manager_streams()
    {
        await using var backend = await CreateBackendAsync();
        var aggregateStream = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(aggregateStream, 0,
            [ContractEnvelopes.Build(
                aggregateStream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
            CancellationToken.None);

        var pmStream = ContractEnvelopes.NewProcessManagerStreamId();
        await backend.Store.AppendProcessManagerEventsAsync(pmStream, 0,
            [ContractEnvelopes.BuildProcessManager(pmStream, 1, new ContractStepRecorded(1))],
            CancellationToken.None);

        var feed = await ReadFeedAsync(backend.Store);

        // Projections derive workflow state from aggregate events and never from PM streams
        // (ADR 0013), so the feed they drive off must not carry them. The second assertion is
        // what makes the first mean something: both rows really landed, and both drew a
        // position, so the feed is filtering rather than the PM append having quietly failed.
        //
        // Provenance: a green-on-write characterization of the position hook, hardened rather
        // than written fresh. HaveCount alone was the whole assertion, and a backend returning
        // any two-element list satisfied it, so a hook that answered with a hardcoded literal
        // passed every core fact. The strengthening is ascending plus distinct; its teeth were
        // proven against a scratch backend built to return exactly that literal, which failed
        // here and was then discarded. Not asserted: gaplessness. ADR 0044 tolerates permanent
        // gaps and SQL Server burns identity-cache positions, so contiguity is not the port's.
        feed.Select(e => e.StreamId).Should().Equal(aggregateStream);
        var committed = await backend.ReadCommittedPositionsAsync();
        committed.Should().HaveCount(2).And.BeInAscendingOrder();
        committed.Distinct().Should().HaveCount(committed.Count);
    }

    [Fact]
    public async Task Reading_committed_positions_spans_both_append_paths_strictly_ascending()
    {
        await using var backend = await CreateBackendAsync();

        // Provenance: a green-on-write characterization, added when the hook's only core
        // assertion turned out to be a count. IEventStoreContractBackend says the hook returns
        // "every committed position, ascending, including the rows ReadAllAsync excludes", and
        // nothing in the core suite held it to any of that. This fact does. Its teeth were
        // proven against a scratch backend whose hook returned a hardcoded two-element list,
        // which failed here and was then discarded.
        //
        // One event per append, each aggregate event on its own stream. Multi-event appends are
        // avoided deliberately: KurrentDB stamps GlobalPosition from the append's commit
        // position, so every event of one call shares it, and distinctness would then be a claim
        // about the engine's batching rather than about the hook. That is why the multi-event
        // fact above pins only BeInAscendingOrder, which admits equals.
        var streamA = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(streamA, 0,
            [ContractEnvelopes.Build(streamA, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m))],
            CancellationToken.None);

        var pmStream = ContractEnvelopes.NewProcessManagerStreamId();
        await backend.Store.AppendProcessManagerEventsAsync(pmStream, 0,
            [ContractEnvelopes.BuildProcessManager(pmStream, 1, new ContractStepRecorded(1))],
            CancellationToken.None);

        var streamB = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(streamB, 0,
            [ContractEnvelopes.Build(streamB, 1, new ContractOrderNoted("second stream"))],
            CancellationToken.None);

        await backend.Store.AppendProcessManagerEventsAsync(pmStream, 1,
            [ContractEnvelopes.BuildProcessManager(pmStream, 2, new ContractStepRecorded(2))],
            CancellationToken.None);

        var positions = await backend.ReadCommittedPositionsAsync();
        var feed = await ReadFeedAsync(backend.Store);

        // Four rows committed across the two append paths; the aggregate feed carries two of
        // them. Both numbers are asserted, because the gap between them is the PM rows and that
        // gap is what the hook exists to see.
        positions.Should().HaveCount(4);
        feed.Should().HaveCount(2);

        // Strictly ascending, spelled as ascending plus distinct. BeInAscendingOrder alone
        // admits equal neighbours, which is what let a degenerate hook through before.
        positions.Should().BeInAscendingOrder();
        positions.Distinct().Should().HaveCount(positions.Count);

        // Every position the feed reports is one the hook reports, and the hook reports two
        // more: the PM rows ReadAllAsync excludes by design. Gaplessness is not asserted here
        // either (ADR 0044).
        positions.Should().Contain(feed.Select(e => e.GlobalPosition));
        (positions.Count - feed.Count).Should().Be(2);
    }

    [Fact]
    public async Task Reading_a_tenant_window_filters_by_tenant_and_honors_its_bounds()
    {
        await using var backend = await CreateBackendAsync();
        var otherTenant = TenantId.From(Guid.NewGuid());

        // Four appends, alternating tenants, each on its own stream so each draws its own
        // position. Positions are read back rather than assumed: the contract promises
        // ascending, never contiguous (ADR 0044).
        var first = await AppendOneAsync(backend.Store, WellKnownTenants.Default);
        var second = await AppendOneAsync(backend.Store, otherTenant);
        var third = await AppendOneAsync(backend.Store, WellKnownTenants.Default);
        var fourth = await AppendOneAsync(backend.Store, otherTenant);

        var defaultTenantFeed = await ReadTenantFeedAsync(
            backend.Store, WellKnownTenants.Default, fromPosition: 0, toPositionInclusive: fourth);
        var otherTenantFeed = await ReadTenantFeedAsync(
            backend.Store, otherTenant, fromPosition: 0, toPositionInclusive: fourth);

        // The filter: neither tenant ever sees the other's events, over the same window.
        defaultTenantFeed.Select(e => e.GlobalPosition)
            .Should().Equal(first, third).And.BeInAscendingOrder();
        otherTenantFeed.Select(e => e.GlobalPosition)
            .Should().Equal(second, fourth).And.BeInAscendingOrder();

        // Both bounds, pinned in one read. fromPosition is exclusive, so the event sitting
        // exactly on it drops out. toPositionInclusive is inclusive, so the event sitting
        // exactly on the ceiling stays in.
        var window = await ReadTenantFeedAsync(
            backend.Store, WellKnownTenants.Default, fromPosition: first, toPositionInclusive: third);

        window.Select(e => e.GlobalPosition).Should().Equal(third);
    }

    [Fact]
    public async Task Appending_process_manager_events_at_a_stale_expected_version_raises_the_concurrency_contract()
    {
        await using var backend = await CreateBackendAsync();
        var stream = ContractEnvelopes.NewProcessManagerStreamId();
        await backend.Store.AppendProcessManagerEventsAsync(stream, 0,
            [ContractEnvelopes.BuildProcessManager(stream, 1, new ContractStepRecorded(1))],
            CancellationToken.None);

        // The PM append path owes the same concurrency contract as the aggregate path: same
        // (stream, version) uniqueness, same let-the-write-fail detection, same store-agnostic
        // exception carrying the same payload.
        var stale = ContractEnvelopes.BuildProcessManager(stream, 1, new ContractStepRecorded(2));
        var act = async () => await backend.Store.AppendProcessManagerEventsAsync(
            stream, 0, [stale], CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ConcurrencyException>()).Which;
        ex.StreamId.Should().Be(stream);
        ex.ExpectedVersion.Should().Be(0);
    }

    [Fact]
    public async Task Reading_all_resumes_from_a_non_zero_position_exclusively()
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

        var feed = await ReadFeedAsync(backend.Store);
        var resumeFrom = feed[0].GlobalPosition;

        var tail = await ReadFeedAsync(backend.Store, resumeFrom);

        // fromPosition is the resume checkpoint and it is exclusive: the event sitting exactly
        // on it is not replayed. A projection resuming at its checkpoint must not reprocess the
        // event it already checkpointed.
        tail.Select(e => e.StreamVersion).Should().Equal(2, 3);
        tail.Select(e => e.GlobalPosition)
            .Should().Equal(feed[1].GlobalPosition, feed[2].GlobalPosition);
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

    private static async Task<IReadOnlyList<EventEnvelope>> ReadTenantFeedAsync(
        IEventStore store, TenantId tenant, long fromPosition, long toPositionInclusive)
    {
        var feed = new List<EventEnvelope>();
        await foreach (var envelope in store.ReadAllForTenantAsync(
            tenant, fromPosition, toPositionInclusive, CancellationToken.None))
        {
            feed.Add(envelope);
        }
        return feed;
    }

    // One event on its own stream for the given tenant, returning the position it drew. The
    // position is read back from the store rather than assumed, because the contract does not
    // promise contiguity.
    private static async Task<long> AppendOneAsync(IEventStore store, TenantId tenant)
    {
        var stream = ContractEnvelopes.NewStreamId();
        await store.AppendAsync(stream, 0,
            [ContractEnvelopes.Build(
                stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m), tenant: tenant)],
            CancellationToken.None);

        var read = await store.ReadStreamAsync(stream, 0, CancellationToken.None);
        return read.Single().GlobalPosition;
    }
}
