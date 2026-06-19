using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Phase 12 operational reader, commit 2: the projection-lag reader. It composes the
// head-position port (IEventStoreHeadPosition) with ICheckpointStore over the
// registered projection roster (IEnumerable<IProjection>) and reports, per
// projection, head minus checkpoint as positions-behind. ProjectionLagReader and
// ProjectionLag do not exist yet; these tests drive them into being, composed
// against the ports and exercised over a migrated Postgres database. The single
// method under test is ReadAsync(CancellationToken).
public class ProjectionLagReaderTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTime BaseTime = new(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public ProjectionLagReaderTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Reader_returns_one_row_per_registered_projection()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var eventStore = CreateEventStore(dataSource);
        await AppendProbeEventsAsync(eventStore);

        var reader = CreateReader(
            dataSource, [new StubProjection("proj-a"), new StubProjection("proj-b")]);
        var result = await reader.ReadAsync(CancellationToken.None);

        result.Select((ProjectionLag row) => row.ProjectionName)
            .Should().BeEquivalentTo("proj-a", "proj-b");
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task Reader_reports_positions_behind_as_head_minus_checkpoint_for_a_behind_projection()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var eventStore = CreateEventStore(dataSource);
        await AppendProbeEventsAsync(eventStore);
        var head = await HeadAsync(dataSource);
        await AdvanceAndCommitAsync(dataSource, "proj-a", head - 1);

        var reader = CreateReader(dataSource, [new StubProjection("proj-a")]);
        var result = await reader.ReadAsync(CancellationToken.None);

        ProjectionLag row = result.Single(r => r.ProjectionName == "proj-a");
        row.HeadPosition.Should().Be(head);
        row.CheckpointPosition.Should().Be(head - 1);
        row.PositionsBehind.Should().Be(1);
    }

    [Fact]
    public async Task Reader_reports_a_caught_up_projection_as_zero_behind()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var eventStore = CreateEventStore(dataSource);
        await AppendProbeEventsAsync(eventStore);
        var head = await HeadAsync(dataSource);
        await AdvanceAndCommitAsync(dataSource, "proj-a", head);

        var reader = CreateReader(dataSource, [new StubProjection("proj-a")]);
        var result = await reader.ReadAsync(CancellationToken.None);

        ProjectionLag row = result.Single(r => r.ProjectionName == "proj-a");
        row.CheckpointPosition.Should().Be(row.HeadPosition);
        row.PositionsBehind.Should().Be(0);
    }

    [Fact]
    public async Task Reader_reports_a_never_run_projection_as_behind_by_the_whole_log()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var eventStore = CreateEventStore(dataSource);
        await AppendProbeEventsAsync(eventStore);
        var head = await HeadAsync(dataSource);

        // proj-a never checkpoints, so no row exists for it.
        var reader = CreateReader(dataSource, [new StubProjection("proj-a")]);
        var result = await reader.ReadAsync(CancellationToken.None);

        ProjectionLag row = result.Single(r => r.ProjectionName == "proj-a");
        row.CheckpointPosition.Should().Be(0);
        row.PositionsBehind.Should().Be(head);
    }

    private static ProjectionLagReader CreateReader(
        NpgsqlDataSource dataSource, IEnumerable<IProjection> projections)
    {
        var headReader = new PostgresEventStoreHeadReader(new NpgsqlConnectionFactory(dataSource));
        var checkpointStore = new PostgresCheckpointStore(new NpgsqlReadModelConnectionFactory(dataSource));
        return new ProjectionLagReader(headReader, checkpointStore, projections);
    }

    private static PostgresEventStore CreateEventStore(NpgsqlDataSource dataSource)
        => new(new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());

    private static Task<long> HeadAsync(NpgsqlDataSource dataSource)
        => new PostgresEventStoreHeadReader(new NpgsqlConnectionFactory(dataSource))
            .GetHeadPositionAsync(CancellationToken.None);

    private static async Task AppendProbeEventsAsync(PostgresEventStore eventStore)
    {
        var stream = StreamId.Parse($"test:{Guid.NewGuid():N}");
        await eventStore.AppendAsync(stream, 0,
        [
            Env(stream, 1, new LagProbe(Guid.NewGuid())),
            Env(stream, 2, new LagProbe(Guid.NewGuid())),
            Env(stream, 3, new LagProbe(Guid.NewGuid())),
        ], CancellationToken.None);
    }

    // Advances one projection's checkpoint in its own committed transaction, the
    // way a projection handler does (PostgresCheckpointStoreTests' pattern).
    private static async Task AdvanceAndCommitAsync(
        NpgsqlDataSource dataSource, string name, long position)
    {
        var store = new PostgresCheckpointStore(new NpgsqlReadModelConnectionFactory(dataSource));
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await store.AdvanceAsync(name, position, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static EventEnvelope Env(StreamId streamId, int version, IDomainEvent payload)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: BaseTime,
            Tenant: WellKnownTenants.Default);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: version,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: BaseTime,
            GlobalPosition: 0);
    }

    private static EventTypeRegistry CreateRegistry()
        => new EventTypeRegistry().Register<LagProbe>();

    private static ProcessManagerEventTypeRegistry CreatePmRegistry()
        => new();

    private static JsonSerializerOptions CreateJsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new TenantIdJsonConverter() },
        };

    private sealed record StubProjection(string Name) : IProjection;

    private sealed record LagProbe(Guid Id) : IDomainEvent;
}
