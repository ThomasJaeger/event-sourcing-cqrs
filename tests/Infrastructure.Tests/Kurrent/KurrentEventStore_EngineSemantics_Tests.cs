using EventSourcingCqrs.EventStore.ContractTests;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Kurrent;

// KurrentDB engine semantics the adapter relies on, pinned through its public IEventStore surface.
// These are green-on-write characterizations of observed behavior (the slice-0 spike's duplicate-id
// and ordering findings), not RED-first specifications: the behaviors are the engine's, already
// present, and the adapter's design is built on them. They are here because that reliance is
// load-bearing, and each states below what shipped change would turn it red.
//
// Same fixture and per-fact isolation as the contract class: a fresh mem-db node per fact.
public class KurrentEventStore_EngineSemantics_Tests : IClassFixture<KurrentFixture>
{
    private readonly KurrentFixture _fixture;

    public KurrentEventStore_EngineSemantics_Tests(KurrentFixture fixture)
    {
        _fixture = fixture;
    }

    // Native per-stream idempotency attaches to the contract's event ids: a retried append (same
    // event id, same expected version) is a no-op rather than a duplicate or a conflict. This is
    // why the adapter passes the contract's event ids straight through as KurrentDB event ids
    // instead of minting fresh ones. Turns red if the adapter ever remaps event ids on append, or
    // if the expected-version mapping stops presenting the retry as the same write.
    [Fact]
    public async Task A_retried_append_of_one_event_id_is_idempotent_on_its_stream()
    {
        await using var backend = await KurrentContractBackend.CreateAsync(_fixture);
        var stream = ContractEnvelopes.NewStreamId();
        var eventId = Guid.NewGuid();

        await backend.Store.AppendAsync(stream, 0,
            [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m), eventId)],
            CancellationToken.None);
        await backend.Store.AppendAsync(stream, 0,
            [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m), eventId)],
            CancellationToken.None);

        var read = await backend.Store.ReadStreamAsync(stream, 0, CancellationToken.None);
        read.Should().HaveCount(1);
    }

    // Idempotency is by whole-append, not by event id within one append: two envelopes sharing an
    // id in a single AppendAsync both land, no intra-call dedup. Turns red if the adapter starts
    // splitting one AppendAsync into multiple writes, or if a future engine version dedups within a
    // batch, either of which would drop the second envelope.
    [Fact]
    public async Task Two_envelopes_sharing_an_event_id_in_one_append_both_land()
    {
        await using var backend = await KurrentContractBackend.CreateAsync(_fixture);
        var stream = ContractEnvelopes.NewStreamId();
        var eventId = Guid.NewGuid();

        await backend.Store.AppendAsync(stream, 0,
            [
                ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m), eventId),
                ContractEnvelopes.Build(stream, 2, new ContractOrderNoted("second"), eventId),
            ],
            CancellationToken.None);

        var read = await backend.Store.ReadStreamAsync(stream, 0, CancellationToken.None);
        read.Should().HaveCount(2);
    }

    // Dedup is per stream, not global: the same event id appended to two different streams lands on
    // both. This is what lets the contract's duplicate-id fact pass, where a shared id crosses
    // streams and must not be swallowed. Turns red if the adapter ever routes two streams onto one
    // KurrentDB stream, collapsing the per-stream boundary the dedup keys on.
    [Fact]
    public async Task One_event_id_appended_to_two_streams_lands_on_both()
    {
        await using var backend = await KurrentContractBackend.CreateAsync(_fixture);
        var streamA = ContractEnvelopes.NewStreamId();
        var streamB = ContractEnvelopes.NewStreamId();
        var eventId = Guid.NewGuid();

        await backend.Store.AppendAsync(streamA, 0,
            [ContractEnvelopes.Build(streamA, 1, new ContractOrderPlaced(Guid.NewGuid(), 10m), eventId)],
            CancellationToken.None);
        await backend.Store.AppendAsync(streamB, 0,
            [ContractEnvelopes.Build(streamB, 1, new ContractOrderNoted("other stream"), eventId)],
            CancellationToken.None);

        (await backend.Store.ReadStreamAsync(streamA, 0, CancellationToken.None)).Should().HaveCount(1);
        (await backend.Store.ReadStreamAsync(streamB, 0, CancellationToken.None)).Should().HaveCount(1);
    }

    // The ordering load probe, deliberately nondeterministic in what it fixes. The commit order of
    // concurrent appends across streams is the engine's to decide and is not asserted; what is
    // asserted is that every append drew a position, the positions are strictly ascending, and none
    // collides. This is the strongest ordering evidence the engine permits absent a held-writer
    // analogue: KurrentDB has no interactive transaction to park an append mid-flight, so the
    // capability split routes the held-writer probes away from this engine and this load probe is
    // what stands in their place. Turns red if two appends were ever to draw the same commit
    // position or a position were to go backwards, which is the log corruption ADR 0044 rules out.
    [Fact]
    public async Task Concurrent_appends_across_streams_draw_strictly_ascending_committed_positions()
    {
        await using var backend = await KurrentContractBackend.CreateAsync(_fixture);
        const int appendCount = 8;

        var appends = Enumerable.Range(0, appendCount).Select(_ =>
        {
            var stream = ContractEnvelopes.NewStreamId();
            return backend.Store.AppendAsync(stream, 0,
                [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
                CancellationToken.None);
        }).ToArray();
        await Task.WhenAll(appends);

        var positions = await backend.ReadCommittedPositionsAsync();

        positions.Should().HaveCount(appendCount).And.BeInAscendingOrder();
        positions.Distinct().Should().HaveCount(appendCount);
    }
}
