using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.Kurrent;
using FluentAssertions;
using KurrentDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Kurrent;

// RED for slice 5's KurrentDB head-position reader, the engine-specific counterpart to
// PostgresEventStoreHeadReader that the AdminConsole's projection-lag read subtracts each projection
// checkpoint from. Both facts drive the reader against a fresh node; it throws NotImplementedException
// until the GREEN slice implements the backwards-from-End read.
public class KurrentEventStoreHeadReaderTests : IClassFixture<KurrentFixture>
{
    private readonly KurrentFixture _fixture;

    public KurrentEventStoreHeadReaderTests(KurrentFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task An_empty_aggregate_feed_reports_head_position_zero()
    {
        var container = await _fixture.StartNodeAsync();
        try
        {
            var (_, client) = Compose(container);
            var reader = new KurrentEventStoreHeadReader(client);

            var head = await reader.GetHeadPositionAsync(CancellationToken.None);

            // The empty aggregate feed maps to 0, the same contract PostgresEventStoreHeadReader gives an
            // empty events table through COALESCE(MAX(global_position), 0).
            head.Should().Be(0);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_head_equals_the_last_committed_aggregate_events_position()
    {
        var container = await _fixture.StartNodeAsync();
        try
        {
            var (store, client) = Compose(container);
            var first = ContractEnvelopes.NewStreamId();
            await store.AppendAsync(first, 0,
                [ContractEnvelopes.Build(first, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
                CancellationToken.None);
            var second = ContractEnvelopes.NewStreamId();
            await store.AppendAsync(second, 0,
                [ContractEnvelopes.Build(second, 1, new ContractOrderNoted("second"))],
                CancellationToken.None);
            var expected = await LastCommittedAggregatePositionAsync(client);

            var reader = new KurrentEventStoreHeadReader(client);
            var head = await reader.GetHeadPositionAsync(CancellationToken.None);

            head.Should().Be(expected);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    // The exclusion's own fact, and the twin of the DynamoDB reader's PM fact, which asserts the
    // opposite because the two engines land on opposite sides of the same split. Green on write, and
    // declared: the filter has excluded PM streams since the reader shipped, and ADR 0047 records the
    // divergence. What was missing is a test. Both facts here appended only aggregate events, so
    // nothing pinned the property that makes this head aggregate-only, and the reader and its ADR were
    // its only evidence.
    //
    // The pairing is the point. On KurrentDB a PM append does not move the head, so the head lands in
    // the projection checkpoints' own space. On DynamoDB it does, so a caught-up projection reports a
    // small permanent lag on a PM-tailed log. Neither is a defect; they are the same question answered
    // by what each engine charges for the filter.
    [Fact]
    public async Task A_process_manager_append_does_not_move_the_head()
    {
        var container = await _fixture.StartNodeAsync();
        try
        {
            var (store, client) = Compose(container);
            var aggregate = ContractEnvelopes.NewStreamId();
            await store.AppendAsync(aggregate, 0,
                [ContractEnvelopes.Build(aggregate, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
                CancellationToken.None);
            var reader = new KurrentEventStoreHeadReader(client);
            var afterAggregate = await reader.GetHeadPositionAsync(CancellationToken.None);

            var pmStream = ContractEnvelopes.NewProcessManagerStreamId();
            await store.AppendProcessManagerEventsAsync(pmStream, 0,
                [ContractEnvelopes.BuildProcessManager(pmStream, 1, new ContractStepRecorded(1))],
                CancellationToken.None);

            var afterPm = await reader.GetHeadPositionAsync(CancellationToken.None);

            afterPm.Should().Be(afterAggregate,
                "the aggregate-feed filter excludes PM streams, so the head stays in the projection "
                + "checkpoints' space");
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    private static (IEventStore Store, KurrentDBClient Client) Compose(
        DotNet.Testcontainers.Containers.IContainer container)
    {
        var connectionString =
            $"esdb://{container.Hostname}:{container.GetMappedPublicPort(2113)}?tls=false";
        var services = new ServiceCollection();
        services.AddSingleton<IEventTypeProvider, ContractDomainEventTypeProvider>();
        services.AddSingleton<IProcessManagerEventTypeProvider, ContractProcessManagerEventTypeProvider>();
        services.AddKurrentEventStore(opts => opts.ConnectionString = connectionString);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IEventStore>(), provider.GetRequiredService<KurrentDBClient>());
    }

    private static async Task<long> LastCommittedAggregatePositionAsync(KurrentDBClient client)
    {
        long max = 0;
        await foreach (var resolved in client.ReadAllAsync(
            Direction.Forwards, Position.Start, StreamFilter.RegularExpression(@"^(?!\$)(?!pm-)")))
        {
            max = Math.Max(max, checked((long)resolved.Event.Position.CommitPosition));
        }
        return max;
    }
}
