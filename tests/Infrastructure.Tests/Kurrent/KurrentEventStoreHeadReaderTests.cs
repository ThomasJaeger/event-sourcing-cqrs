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
