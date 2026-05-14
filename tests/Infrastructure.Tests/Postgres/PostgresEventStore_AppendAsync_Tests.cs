using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class PostgresEventStore_AppendAsync_Tests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresEventStore_AppendAsync_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Append_writes_single_event_to_events_table()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        var envelope = BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 42.5m));

        await store.AppendAsync(streamId, 0, [envelope], CancellationToken.None);

        var rows = await ReadEventsAsync(connStr, streamId);
        rows.Should().HaveCount(1);
        rows[0].StreamVersion.Should().Be(1);
        rows[0].EventId.Should().Be(envelope.EventId);
        rows[0].EventType.Should().Be(nameof(TestPayload));
    }

    [Fact]
    public async Task Append_writes_outbox_row_alongside_event_in_same_transaction()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        var envelope = BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 7m));

        await store.AppendAsync(streamId, 0, [envelope], CancellationToken.None);

        (await CountAsync(connStr, "event_store.events")).Should().Be(1);
        (await CountAsync(connStr, "event_store.outbox")).Should().Be(1);
        var outboxEventId = await ScalarAsync<Guid>(
            connStr, "SELECT event_id FROM event_store.outbox");
        outboxEventId.Should().Be(envelope.EventId);

        // The outbox row carries the events row's IDENTITY-assigned
        // global_position, threaded through the same transaction via RETURNING.
        var eventGlobalPosition = await ScalarAsync<long>(
            connStr, "SELECT global_position FROM event_store.events");
        var outboxGlobalPosition = await ScalarAsync<long>(
            connStr, "SELECT global_position FROM event_store.outbox");
        outboxGlobalPosition.Should().Be(eventGlobalPosition);
    }

    [Fact]
    public async Task Append_writes_multiple_events_with_sequential_stream_version()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        EventEnvelope[] envelopes =
        [
            BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m)),
            BuildEnvelope(streamId, 2, new TestPayload(Guid.NewGuid(), 2m)),
            BuildEnvelope(streamId, 3, new TestPayload(Guid.NewGuid(), 3m)),
        ];

        await store.AppendAsync(streamId, 0, envelopes, CancellationToken.None);

        var rows = await ReadEventsAsync(connStr, streamId);
        rows.Select(r => r.StreamVersion).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Append_to_existing_stream_continues_version_sequence()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        EventEnvelope[] first =
        [
            BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m)),
            BuildEnvelope(streamId, 2, new TestPayload(Guid.NewGuid(), 2m)),
        ];
        EventEnvelope[] second =
        [
            BuildEnvelope(streamId, 3, new TestPayload(Guid.NewGuid(), 3m)),
            BuildEnvelope(streamId, 4, new TestPayload(Guid.NewGuid(), 4m)),
        ];

        await store.AppendAsync(streamId, 0, first, CancellationToken.None);
        await store.AppendAsync(streamId, 2, second, CancellationToken.None);

        var rows = await ReadEventsAsync(connStr, streamId);
        rows.Select(r => r.StreamVersion).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Append_throws_ConcurrencyException_when_stream_version_taken()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        await store.AppendAsync(
            streamId,
            0,
            [BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m))],
            CancellationToken.None);

        // A second writer that thinks the stream is still at version 0
        // computes stream_version=1 for its first envelope. The unique
        // constraint on (stream_id, stream_version) fires and the adapter
        // maps the 23505 to ConcurrencyException.
        var stale = BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 2m));
        var act = async () =>
            await store.AppendAsync(streamId, 0, [stale], CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ConcurrencyException>()).Which;
        ex.StreamId.Should().Be(streamId);
        ex.ExpectedVersion.Should().Be(0);

        // The original event survives; the failing batch did not partially apply.
        (await CountAsync(connStr, "event_store.events")).Should().Be(1);
        (await CountAsync(connStr, "event_store.outbox")).Should().Be(1);
    }

    [Fact]
    public async Task Append_round_trips_payload_through_jsonb_serialization()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var payload = new TestPayload(Guid.NewGuid(), 99.95m);
        var envelope = BuildEnvelope(streamId, 1, payload, correlationId: correlationId);

        await store.AppendAsync(streamId, 0, [envelope], CancellationToken.None);

        // Raw JSONB inspection: snake_case keys are what the generated
        // correlation_id/causation_id columns extract on. If the policy
        // ever drifts to camelCase or PascalCase, both this assertion and
        // the generated column will silently break together; the assertion
        // here catches it first.
        var payloadText = await ScalarAsync<string>(
            connStr, "SELECT payload::text FROM event_store.events");
        payloadText.Should().Contain("\"order_id\":");
        payloadText.Should().Contain("\"total\":");
        payloadText.Should().NotContain("OrderId");

        var generatedCorrelationId = await ScalarAsync<Guid>(
            connStr, "SELECT correlation_id FROM event_store.events");
        generatedCorrelationId.Should().Be(correlationId);

        // ReadStream round-trip: deserialized payload equals original.
        var read = await store.ReadStreamAsync(streamId, 0, CancellationToken.None);
        read.Should().HaveCount(1);
        read[0].Payload.Should().BeOfType<TestPayload>().Which.Should().Be(payload);
        read[0].Metadata.CorrelationId.Should().Be(correlationId);
        read[0].OccurredUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Append_propagates_unique_violation_on_event_id_unchanged()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();
        var sharedEventId = Guid.NewGuid();

        await store.AppendAsync(
            streamA,
            0,
            [BuildEnvelope(streamA, 1, new TestPayload(Guid.NewGuid(), 1m), eventId: sharedEventId)],
            CancellationToken.None);

        // Same event_id reused against a different stream: the unique
        // violation is on uq_events_event_id, not uq_events_stream_version.
        // The adapter does not silently remap this to ConcurrencyException;
        // a duplicate event ID is a programming bug, not a concurrency
        // retry-and-replay condition.
        var collision = BuildEnvelope(
            streamB, 1, new TestPayload(Guid.NewGuid(), 2m), eventId: sharedEventId);
        var act = async () =>
            await store.AppendAsync(streamB, 0, [collision], CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        ex.ConstraintName.Should().Be("uq_events_event_id");
    }

    private static async Task<int> CountAsync(string connStr, string qualifiedTable)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {qualifiedTable}";
        var result = (long)(await cmd.ExecuteScalarAsync())!;
        return (int)result;
    }

    private static async Task<T> ScalarAsync<T>(string connStr, string sql)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return (T)result!;
    }

    private static async Task<IReadOnlyList<(int StreamVersion, Guid EventId, string EventType)>>
        ReadEventsAsync(string connStr, Guid streamId)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT stream_version, event_id, event_type FROM event_store.events " +
            "WHERE stream_id = @stream_id ORDER BY stream_version";
        cmd.Parameters.AddWithValue("stream_id", streamId);
        var rows = new List<(int, Guid, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetGuid(1), reader.GetString(2)));
        }
        return rows;
    }
}
