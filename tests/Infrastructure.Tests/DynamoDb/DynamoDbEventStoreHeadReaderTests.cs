using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.DynamoDb;

// RED for the DynamoDB head-position reader, the engine-specific counterpart to
// PostgresEventStoreHeadReader and KurrentEventStoreHeadReader that the AdminConsole's
// projection-lag read subtracts each projection checkpoint from. Every fact drives the reader
// against LocalStack; it throws NotImplementedException until the GREEN slice implements the log
// partition's tail read.
//
// THE PM QUESTION, AND THE CROSS-ENGINE SPLIT IT LANDS ON. The log partition carries process-manager
// rows as well as aggregate rows: both append paths funnel through AppendRowsAsync, so the committed
// position sequence spans both, and the exclusion is a read-side filter rather than a write-side one
// (DynamoDbEventStore's IsAggregateRow, applied by ReadAllAsync and nowhere else). That leaves this
// reader a choice the other two engines have already made in opposite directions. The Postgres head
// is MAX(global_position) over the shared events table and spans PM rows. The Kurrent head reads
// $all through the aggregate-feed filter and does not. ADR 0047 records that divergence from
// KurrentDB's side; ADR 0049 records this engine's mappings, and the head's span belongs there.
//
// These facts pin the unfiltered head: the tail of the log, PM rows included, which puts DynamoDB on
// the relational side of the split. The cost is named rather than hidden. Projections checkpoint at
// positions drawn from ReadAllAsync, which skips PM rows, so a PM-tailed log reports a head no
// projection can reach and a caught-up projection shows a small permanent lag. That is exactly the
// characteristic PostgreSQL already has and KurrentDB deliberately does not, so this is a choice
// about which engine to match rather than a defect to fix here. It is worth a ruling before GREEN.
public class DynamoDbEventStoreHeadReaderTests : IClassFixture<LocalStackFixture>
{
    private readonly LocalStackFixture _fixture;

    public DynamoDbEventStoreHeadReaderTests(LocalStackFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_empty_store_reports_head_position_zero()
    {
        await using var backend = await DynamoDbContractBackend.CreateAsync(_fixture);

        var head = await backend.HeadReader.GetHeadPositionAsync(CancellationToken.None);

        // The empty store maps to 0, the contract the port's comment states and the same answer
        // PostgresEventStoreHeadReader gives an empty events table through COALESCE to 0. The counter
        // row is seeded at zero and holds no position, so an empty log is an empty Query rather than
        // a missing table.
        head.Should().Be(0);
    }

    [Fact]
    public async Task The_head_equals_the_last_committed_events_position()
    {
        await using var backend = await DynamoDbContractBackend.CreateAsync(_fixture);
        var first = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(first, 0,
            [ContractEnvelopes.Build(first, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
            CancellationToken.None);
        var second = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(second, 0,
            [ContractEnvelopes.Build(second, 1, new ContractOrderNoted("second"))],
            CancellationToken.None);
        var expected = (await backend.ReadCommittedPositionsAsync())[^1];

        var head = await backend.HeadReader.GetHeadPositionAsync(CancellationToken.None);

        head.Should().Be(expected);
    }

    // The PM fact, and the one that decides the split. A PM append draws from the same counter and
    // writes the same log row an aggregate append does, so it moves the tail. This asserts the head
    // follows it, which is the Postgres reading.
    [Fact]
    public async Task A_process_manager_append_moves_the_head_because_the_log_carries_both_families()
    {
        await using var backend = await DynamoDbContractBackend.CreateAsync(_fixture);
        var aggregate = ContractEnvelopes.NewStreamId();
        await backend.Store.AppendAsync(aggregate, 0,
            [ContractEnvelopes.Build(aggregate, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
            CancellationToken.None);
        var afterAggregate = await backend.HeadReader.GetHeadPositionAsync(CancellationToken.None);

        var pmStream = ContractEnvelopes.NewProcessManagerStreamId();
        await backend.Store.AppendProcessManagerEventsAsync(pmStream, 0,
            [ContractEnvelopes.BuildProcessManager(pmStream, 1, new ContractStepRecorded(1))],
            CancellationToken.None);

        var afterPm = await backend.HeadReader.GetHeadPositionAsync(CancellationToken.None);

        afterPm.Should().BeGreaterThan(afterAggregate,
            "the log partition carries PM rows, so a PM append advances the tail this head reads");
    }
}
