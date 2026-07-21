using System.Diagnostics;
using Amazon.DynamoDBv2.Model;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Tests.DynamoDb;

// The engine facts the contract suite cannot reach, homed with the engine that owns them. Mirrors
// KurrentEventStore_EngineSemantics_Tests' role: the shared suite pins what the port owes, and this
// pins what this engine does underneath it.
//
// Provenance, per fact. The storm probe is a genuine probe: it drives concurrency the suite never
// creates, and its assertions can fail on engine behavior. The budget boundary and the dual-write
// agreement are green-on-write characterizations of shipped behavior, and each header names what
// turns it red. The translation facts are deterministic unit-level pins over a fake client; their
// teeth were proven against a scratch fake that shifted the reason array by one index, which failed
// them and was then discarded.
//
// Same fixture and per-fact isolation as the contract class: a fresh table per backend on the
// shared LocalStack node.
public class DynamoDbEventStore_EngineSemantics_Tests : IClassFixture<LocalStackFixture>
{
    private readonly LocalStackFixture _fixture;
    private readonly ITestOutputHelper _output;

    public DynamoDbEventStore_EngineSemantics_Tests(LocalStackFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Concurrent_appends_across_streams_draw_a_gapless_strictly_ascending_sequence()
    {
        // The probe the contract suite cannot be: twelve writers on distinct streams, all
        // contending for the one counter row that serializes position assignment. Every append
        // goes through the production store, so the retry loop, the backoff, and the positional
        // translation are all under load here and nowhere else.
        //
        // Gapless is this engine's honest pin, and it is stronger than what ADR 0044 requires.
        // The ADR tolerates permanent gaps because a rolled-back relational append burns the
        // positions it drew: PostgreSQL IDENTITY and SQL Server's identity cache both hand out
        // numbers that never come back. This engine cannot burn one. A position is drawn by a
        // conditional counter update inside the append's own transaction, so a cancelled
        // transaction rolls the increment back with everything else and the next writer draws the
        // same number. Nothing here consumes a position without committing it. A gap would mean a
        // committed transaction that did not advance the counter, or an advance that did not write
        // its log row, and either is a defect rather than a tolerated engine behavior. The spike
        // observed the same: 300 appends, positions 1..300, no gaps.
        await using var backend = await DynamoDbContractBackend.CreateAsync(_fixture);

        const int writers = 12;
        const int appendsPerWriter = 25;
        const int expected = writers * appendsPerWriter;

        var sw = Stopwatch.StartNew();
        var work = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < appendsPerWriter; i++)
            {
                var stream = ContractEnvelopes.NewStreamId();
                await backend.Store.AppendAsync(stream, 0,
                    [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
                    CancellationToken.None);
            }
        })).ToArray();

        // Every appender completing is itself an assertion: a writer that exhausted the default
        // attempt cap would surface here as DynamoDbPositionContentionException rather than as a
        // count mismatch. At 256 that exception is a defect, because chance exhaustion is negligible
        // by construction there and no amount of slow hardware reaches it.
        //
        // The sentence this replaces read the exception as evidence the cap was wrong, and at 64 it
        // was right to: (11/12)^64 put a 300-append run's odds of starving somebody near two in
        // three, so the probe was adjudicating its own margin rather than the engine. That premise
        // was the defect, and it failed on CI before it was found here.
        await Task.WhenAll(work);
        sw.Stop();

        var positions = await backend.ReadCommittedPositionsAsync();

        _output.WriteLine(
            $"{writers} writers x {appendsPerWriter} appends = {expected} in " +
            $"{sw.Elapsed.TotalSeconds:F2}s ({expected / sw.Elapsed.TotalSeconds:F1} appends/s)");

        positions.Should().HaveCount(expected);
        positions.Should().BeInAscendingOrder();
        positions.Distinct().Should().HaveCount(expected);
        positions.Should().Equal(Enumerable.Range(1, expected).Select(i => (long)i),
            "a cancelled transaction rolls its counter increment back, so this engine cannot burn a position");
    }

    [Fact]
    public async Task An_append_at_the_item_budget_succeeds_and_one_past_it_is_refused_whole()
    {
        // The boundary is the fact's teeth. TransactWriteItems allows 100 items and this adapter
        // spends 1 + 3n, so 33 events fit exactly and 34 do not. Asserting only the throw would
        // pass against a guard that refused everything; asserting only the success would pass
        // against no guard at all. Both sides pin the arithmetic.
        //
        // Green on write: the guard shipped with the adapter and no contract fact reaches it,
        // because the suite's largest append is three events.
        await using var backend = await DynamoDbContractBackend.CreateAsync(_fixture);

        var atBudget = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(atBudget, 0, BuildBatch(atBudget, 33), CancellationToken.None);
        (await backend.Store.ReadStreamAsync(atBudget, 0, CancellationToken.None))
            .Should().HaveCount(33);

        var overBudget = ContractEnvelopes.NewStreamId();
        var act = async () => await backend.Store.AppendAsync(
            overBudget, 0, BuildBatch(overBudget, 34), CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<DynamoDbAppendTooLargeException>()).Which;
        ex.EventCount.Should().Be(34);
        ex.ItemCount.Should().Be(103);
        ex.ItemLimit.Should().Be(100);

        // Refused whole, before any write. The stream is empty and the refused append left no log
        // row behind, so the guard is a boundary check rather than a partial write that gave up.
        (await backend.Store.ReadStreamAsync(overBudget, 0, CancellationToken.None))
            .Should().BeEmpty();
        var feed = new List<EventEnvelope>();
        await foreach (var e in backend.Store.ReadAllAsync(0, CancellationToken.None))
        {
            feed.Add(e);
        }
        feed.Should().OnlyContain(e => e.StreamId == atBudget);
    }

    [Fact]
    public async Task The_stream_row_and_the_log_row_hydrate_to_the_same_envelope()
    {
        // Every event is stored twice: once on its stream partition for ReadStreamAsync, once on
        // the log partition for ReadAllAsync. Two row constructions, one truth. Nothing in the
        // engine enforces that they agree, so this fact does.
        //
        // Green on write, and what turns it red is precise: either EventRow or LogRow drifting
        // from the other. Adding an attribute to one and not the other, or writing a different
        // value for the same field, fails here rather than surfacing as a projection that
        // disagrees with a rehydrated aggregate in production.
        await using var backend = await DynamoDbContractBackend.CreateAsync(_fixture);

        var stream = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(stream, 0,
            [
                ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 42m)),
                ContractEnvelopes.Build(stream, 2, new ContractOrderNoted("second")),
            ],
            CancellationToken.None);

        var fromStream = await backend.Store.ReadStreamAsync(stream, 0, CancellationToken.None);
        var fromLog = new List<EventEnvelope>();
        await foreach (var e in backend.Store.ReadAllAsync(0, CancellationToken.None))
        {
            fromLog.Add(e);
        }

        fromStream.Should().HaveCount(2);
        fromLog.Should().HaveCount(2);

        // Field by field rather than record equality, so a failure names the field that drifted.
        foreach (var (s, l) in fromStream.Zip(fromLog))
        {
            l.StreamId.Should().Be(s.StreamId);
            l.StreamVersion.Should().Be(s.StreamVersion);
            l.EventId.Should().Be(s.EventId);
            l.EventType.Should().Be(s.EventType);
            l.EventVersion.Should().Be(s.EventVersion);
            // Record equality, as the contract suite compares payloads. BeEquivalentTo compares
            // against the declared type, and IDomainEvent has no members, so it finds nothing.
            l.Payload.Should().Be(s.Payload);
            l.Metadata.Should().Be(s.Metadata);
            l.OccurredUtc.Should().Be(s.OccurredUtc);
            l.GlobalPosition.Should().Be(s.GlobalPosition);
        }
    }

    [Theory]
    [MemberData(nameof(TranslationCases))]
    public void A_cancellation_reason_translates_by_its_item_index(
        string because, int failedIndex, CancellationVerdict expected)
    {
        // Direct facts against the translation seam. It is a pure function over a reason array, so
        // these need no client at all: constructing the array the engine would return is the whole
        // arrangement. The engine cannot produce these shapes on demand anyway, and the array shape
        // is what the spike measured against the live engine.
        //
        // The layout under test is DynamoDbSchema's documented invariant, for a one-event append:
        // index 0 the counter, index 1 the event row, index 2 the log row, index 3 the event-id
        // row. Teeth proven against a variant that shifted the array by one index, which inverted
        // every verdict.
        var reasons = ReasonsWithFailureAt(failedIndex, itemCount: 4);

        DynamoDbCancellationTranslator.Translate(reasons, eventCount: 1)
            .Should().Be(expected, because);
    }

    public static TheoryData<string, int, CancellationVerdict> TranslationCases() => new()
    {
        {
            "an event row failing is a taken version, which the append turns into the concurrency contract",
            1, CancellationVerdict.VersionConflict
        },
        {
            "an id row failing is a reused event id, which propagates untranslated so no handler retries a bug",
            3, CancellationVerdict.Propagate
        },
        {
            "a counter row failing is another writer taking the position, which is retryable",
            0, CancellationVerdict.Contention
        },
        {
            "a log row failing is the same story as the counter: the log keys on the position the counter handed out",
            2, CancellationVerdict.Contention
        },
    };

    [Fact]
    public void An_unrecognized_cancellation_propagates_rather_than_retrying()
    {
        // Nothing failed, which is a shape the translator has no story for. Propagating beats
        // guessing: a silent retry on an unknown failure is how an append spins forever on
        // something structural.
        var reasons = Enumerable.Range(0, 4)
            .Select(_ => new CancellationReason { Code = "None" })
            .ToList();

        DynamoDbCancellationTranslator.Translate(reasons, eventCount: 1)
            .Should().Be(CancellationVerdict.Propagate);
    }

    private static List<CancellationReason> ReasonsWithFailureAt(int failedIndex, int itemCount)
        => Enumerable.Range(0, itemCount)
            .Select(i => i == failedIndex
                ? new CancellationReason
                {
                    Code = "ConditionalCheckFailed",
                    Message = "The conditional request failed",
                }
                : new CancellationReason { Code = "None" })
            .ToList();

    [Fact]
    public async Task Exhausting_the_attempt_cap_names_the_cap()
    {
        // The cap's own fact, and the one place in this class that still stands a client in. The
        // translation facts above dropped theirs when the mapping became a pure seam; this one
        // cannot, because what it pins is the loop's behavior over many attempts rather than one
        // decision, and a loop needs something to call.
        //
        // The stand-in is the TDD_RULES §3 carve-out, and it holds only because every condition is
        // met: it is a derived stand-in of a client this repository does not own, it exists for an
        // error path the live engine cannot deterministically produce (256 real losses is a load
        // test), it is confined to a reason-array shape the spike measured, it is named here, and
        // it replaces no live-engine fact. What DynamoDB does under contention is pinned by the
        // storm probe against LocalStack, not by this.
        //
        // The counter index fails forever, so every attempt loses. Configured small, and the cap
        // this fact pins is its own rather than the shipped one: reaching 256 against a stand-in
        // would be 256 pointless round trips to learn what three already prove.
        var store = BuildStoreOverFake(
            new FakeCancellingDynamoDb(failedIndex: 0, itemCount: 4), maxAttempts: 3);

        var stream = ContractEnvelopes.NewStreamId();
        var act = async () => await store.AppendAsync(stream, 0,
            [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
            CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<DynamoDbPositionContentionException>()).Which;
        ex.Attempts.Should().Be(3);
        ex.Message.Should().Contain("3 attempts");
        ex.InnerException.Should().BeOfType<TransactionCanceledException>(
            "the exhaustion carries the last real cancellation rather than replacing it");
    }

    private static IReadOnlyList<EventEnvelope> BuildBatch(StreamId stream, int count)
        => Enumerable.Range(1, count)
            .Select(v => ContractEnvelopes.Build(stream, v, new ContractOrderNoted($"e{v}")))
            .ToList();

    // maxAttempts is required rather than defaulted. Its default mirrored the shipped cap with nothing
    // binding the two, and the only caller has always passed its own, so the default was dead the day
    // it was written and stale the day the cap moved.
    private static DynamoDbEventStore BuildStoreOverFake(
        FakeCancellingDynamoDb fake, int maxAttempts)
    {
        var registry = new Infrastructure.Versioning.EventTypeRegistry();
        foreach (var t in ContractEventTypes.DomainEvents)
        {
            registry.Register(t);
        }
        var pmRegistry = new Infrastructure.Versioning.ProcessManagerEventTypeRegistry();
        foreach (var t in ContractEventTypes.ProcessManagerEvents)
        {
            pmRegistry.Register(t);
        }

        return new DynamoDbEventStore(
            fake,
            registry,
            pmRegistry,
            Infrastructure.Versioning.EventStoreJsonOptions.Create(),
            Microsoft.Extensions.Options.Options.Create(new DynamoDbEventStoreOptions
            {
                TableName = "fake",
                MaxAppendAttempts = maxAttempts,
            }),
            new Infrastructure.Versioning.EventUpcasterPipeline(registry, []));
    }
}
