using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class PostgresEventStore_ProcessManager_Tests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private static readonly DateTime At = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    public PostgresEventStore_ProcessManager_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static PostgresEventStore NewStore(NpgsqlDataSource dataSource)
        => new(
            new NpgsqlConnectionFactory(dataSource),
            new EventTypeRegistry(),
            new ProcessManagerEventTypeRegistry().Register<PmTestEvent>(),
            CreateJsonOptions());

    private static StreamId PmStream()
        => StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, Guid.NewGuid());

    [Fact]
    public async Task Append_then_read_round_trips_pm_events()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        var stream = PmStream();

        await store.AppendProcessManagerEventsAsync(
            stream, 0, Envelopes(stream, 2), CancellationToken.None);

        var read = await store.ReadProcessManagerStreamAsync(stream, 0, CancellationToken.None);

        read.Should().HaveCount(2);
        read.Select(e => e.StreamVersion).Should().Equal(1, 2);
        read[0].Payload.Should().BeOfType<PmTestEvent>();
        read.Select(e => e.GlobalPosition).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Append_writes_no_outbox_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);

        await store.AppendProcessManagerEventsAsync(
            PmStream(), 0, Envelopes(PmStream(), 1), CancellationToken.None);

        (await CountAsync(connStr, "event_store.events")).Should().Be(1);
        (await CountAsync(connStr, "event_store.outbox")).Should().Be(0);
    }

    [Fact]
    public async Task ReadAllAsync_excludes_pm_streams()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);

        await store.AppendProcessManagerEventsAsync(
            PmStream(), 0, Envelopes(PmStream(), 2), CancellationToken.None);

        var all = new List<EventEnvelope>();
        await foreach (var e in store.ReadAllAsync(0, CancellationToken.None))
        {
            all.Add(e);
        }

        all.Should().BeEmpty();
    }

    [Fact]
    public async Task Append_throws_ConcurrencyException_on_taken_version()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        var stream = PmStream();
        await store.AppendProcessManagerEventsAsync(
            stream, 0, Envelopes(stream, 1), CancellationToken.None);

        var act = async () => await store.AppendProcessManagerEventsAsync(
            stream, 0, Envelopes(stream, 1), CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ConcurrencyException>()).Which;
        ex.StreamId.Should().Be(stream);
    }

    [Fact]
    public async Task ReadProcessManagerStream_throws_on_a_non_pm_stream()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);

        var act = async () => await store.ReadProcessManagerStreamAsync(
            StreamId.Parse($"order:{Guid.NewGuid():N}"), 0, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadProcessManagerStream_filters_by_from_version()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        var stream = PmStream();
        await store.AppendProcessManagerEventsAsync(
            stream, 0, Envelopes(stream, 3), CancellationToken.None);

        var read = await store.ReadProcessManagerStreamAsync(stream, 2, CancellationToken.None);

        read.Should().HaveCount(1);
        read[0].StreamVersion.Should().Be(3);
    }

    private static IReadOnlyList<ProcessManagerEventEnvelope> Envelopes(StreamId streamId, int count)
    {
        var envelopes = new ProcessManagerEventEnvelope[count];
        for (int i = 0; i < count; i++)
        {
            var eventId = Guid.NewGuid();
            var metadata = new EventMetadata(
                EventId: eventId,
                CorrelationId: Guid.NewGuid(),
                CausationId: Guid.NewGuid(),
                ActorId: Guid.Empty,
                Source: "test",
                SchemaVersion: 1,
                OccurredUtc: At);
            envelopes[i] = new ProcessManagerEventEnvelope(
                StreamId: streamId,
                StreamVersion: i + 1,
                EventId: eventId,
                EventType: nameof(PmTestEvent),
                EventVersion: 1,
                Payload: new PmTestEvent(i),
                Metadata: metadata,
                OccurredUtc: At,
                GlobalPosition: 0);
        }
        return envelopes;
    }

    private static async Task<int> CountAsync(string connStr, string qualifiedTable)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {qualifiedTable}";
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed record PmTestEvent(int Step) : IProcessManagerEvent;
}
