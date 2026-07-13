using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// The commit-visibility invariant: no transaction may hold an assigned-but-uncommitted
// global_position while a transaction holding a higher position commits. Event rows become
// visible in global_position order, so any gap a committed reader observes is permanent (a
// rolled-back append burns a position), never transient.
//
// It matters because IDENTITY draws a position when the INSERT executes, not when the
// transaction commits. Without serialization, a reader tailing the log observes N+1 while N
// is still uncommitted, advances its checkpoint past N, and never reads N once it lands.
// Checkpoints only move forward, so that row is skipped permanently.
//
// Both facts park a writer mid-transaction, holding the append serialization lock with a
// position already drawn, and race a real append against it. The position sequence spans both
// append paths, so the invariant is probed on each.
public class PostgresEventStore_CommitVisibility_Tests : IClassFixture<PostgresFixture>
{
    // Long enough for an unserialized append to finish against a local container. Neither
    // fact asserts on what completes inside it, so a slow machine costs latency, not a
    // false result; the window only gives today's racing append room to land.
    private static readonly TimeSpan RacingAppendWindow = TimeSpan.FromSeconds(2);

    private readonly PostgresFixture _fixture;

    public PostgresEventStore_CommitVisibility_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Aggregate_append_racing_a_held_writer_stays_in_global_position_order()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());

        await using var held = await HeldWriter.StartAsync(connStr, NewStreamId());

        var streamB = NewStreamId();
        var racingAppend = store.AppendAsync(streamB, 0,
            [BuildEnvelope(streamB, 1, new TestPayload(Guid.NewGuid(), 2m))],
            CancellationToken.None);
        await Task.WhenAny(racingAppend, Task.Delay(RacingAppendWindow));

        // Both streams are aggregate streams, so ReadAllAsync sees exactly what a projection
        // driving off it would see. This is the reader that does the skipping.
        var observedBeforeCommit = await ReadFeedPositionsAsync(store);

        await held.CommitAsync();
        await racingAppend;

        var observedAfterCommit = await ReadFeedPositionsAsync(store);

        AssertBecameVisibleInPositionOrder(observedBeforeCommit, observedAfterCommit);
    }

    [Fact]
    public async Task Process_manager_append_racing_a_held_writer_stays_in_global_position_order()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());

        await using var held = await HeldWriter.StartAsync(connStr, NewStreamId());

        var pmStream = NewPmStreamId();
        var racingAppend = store.AppendProcessManagerEventsAsync(pmStream, 0,
            [BuildPmEnvelope(pmStream, 1, new TestPmPayload(1))],
            CancellationToken.None);
        await Task.WhenAny(racingAppend, Task.Delay(RacingAppendWindow));

        var observedBeforeCommit = await ReadCommittedPositionsAsync(connStr);

        await held.CommitAsync();
        await racingAppend;

        var observedAfterCommit = await ReadCommittedPositionsAsync(connStr);

        AssertBecameVisibleInPositionOrder(observedBeforeCommit, observedAfterCommit);
    }

    // The invariant, and nothing else: every position that surfaces once the held writer
    // commits must sit above everything an earlier committed read already observed. A
    // position landing below that high-water mark is one a checkpointing reader has already
    // read past and will never come back for.
    private static void AssertBecameVisibleInPositionOrder(
        IReadOnlyList<long> observedBeforeCommit, IReadOnlyList<long> observedAfterCommit)
    {
        // Positions start at 1, so 0 stands for "nothing observed yet".
        var highWater = observedBeforeCommit.DefaultIfEmpty(0L).Max();
        var surfacedLate = observedAfterCommit.Except(observedBeforeCommit).Order().ToArray();

        surfacedLate.Should().OnlyContain(
            position => position > highWater,
            "a position becoming visible at or below the high-water mark {0} a reader had "
            + "already observed is one it has checkpointed past and will never read. "
            + "Observed before the held writer committed: [{1}]. Surfaced after it committed: "
            + "[{2}].",
            highWater,
            string.Join(", ", observedBeforeCommit),
            string.Join(", ", surfacedLate));

        observedAfterCommit.Should().HaveCount(2).And.BeInAscendingOrder();
    }

    // The aggregate feed, which is what every projection reads.
    private static async Task<IReadOnlyList<long>> ReadFeedPositionsAsync(PostgresEventStore store)
    {
        var positions = new List<long>();
        await foreach (var envelope in store.ReadAllAsync(0, CancellationToken.None))
        {
            positions.Add(envelope.GlobalPosition);
        }
        return positions;
    }

    // ReadAllAsync filters pm- streams out (ADR 0013), so it is structurally blind to the PM
    // fact's racing append and would make that probe vacuous. The invariant is a property of
    // the position sequence, which both append paths draw from, so the PM fact reads the
    // committed positions straight off the events table instead.
    private static async Task<IReadOnlyList<long>> ReadCommittedPositionsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT global_position FROM event_store.events ORDER BY global_position";

        var positions = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            positions.Add(reader.GetInt64(0));
        }
        return positions;
    }

    // A writer parked mid-transaction: it holds the append serialization lock and has drawn a
    // global_position, but has not committed. This is what any real append looks like from the
    // outside between its INSERT and its COMMIT, and it is the state a racing append has to
    // wait behind. The INSERT mirrors the adapter's own; the outbox row the adapter also
    // writes is omitted, because neither reader under test reads the outbox.
    private sealed class HeldWriter : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;

        private HeldWriter(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public static async Task<HeldWriter> StartAsync(string connectionString, StreamId streamId)
        {
            var connection = new NpgsqlConnection(connectionString);
            try
            {
                await connection.OpenAsync();
                var transaction = await connection.BeginTransactionAsync();

                await using (var takeLock = connection.CreateCommand())
                {
                    takeLock.Transaction = transaction;
                    takeLock.CommandText = "SELECT pg_advisory_xact_lock(@key)";
                    takeLock.Parameters.AddWithValue(
                        "key", NpgsqlDbType.Bigint, PostgresEventStore.AppendAdvisoryLockKey);
                    await takeLock.ExecuteNonQueryAsync();
                }

                await InsertOneEventAsync(connection, transaction, streamId);
                return new HeldWriter(connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task CommitAsync() => _transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static async Task InsertOneEventAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, StreamId streamId)
        {
            var envelope = BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m));
            var jsonOptions = CreateJsonOptions();
            var eventTypeName = CreateRegistry().NameFor(envelope.Payload.GetType());

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO event_store.events " +
                "(stream_id, stream_version, event_id, event_type, event_version, " +
                "payload, metadata, occurred_utc) " +
                "VALUES (@stream_id, @stream_version, @event_id, @event_type, " +
                "@event_version, @payload, @metadata, @occurred_utc)";
            insert.Parameters.AddWithValue(
                "stream_id", NpgsqlDbType.Text, envelope.StreamId.Value);
            insert.Parameters.AddWithValue(
                "stream_version", NpgsqlDbType.Integer, envelope.StreamVersion);
            insert.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, envelope.EventId);
            insert.Parameters.AddWithValue("event_type", NpgsqlDbType.Text, eventTypeName);
            insert.Parameters.AddWithValue(
                "event_version", NpgsqlDbType.Smallint, (short)envelope.EventVersion);
            insert.Parameters.AddWithValue(
                "payload",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(envelope.Payload, envelope.Payload.GetType(), jsonOptions));
            insert.Parameters.AddWithValue(
                "metadata",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(envelope.Metadata, jsonOptions));
            insert.Parameters.AddWithValue(
                "occurred_utc", NpgsqlDbType.TimestampTz, envelope.OccurredUtc);

            await insert.ExecuteNonQueryAsync();
        }
    }
}
