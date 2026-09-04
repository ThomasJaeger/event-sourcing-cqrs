using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using EventSourcingCqrs.Infrastructure.Versioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// The SQL Server outbox, at the same seams as the PostgreSQL processor's tests. Every behavioral
// fact there has a twin here except the two notification facts, which have nothing to test: this
// engine has no LISTEN/NOTIFY and ADR 0045's engine mapping, which leaves engine-native triggers unused, scopes it to polling, so the wake path is the idle
// timer and there is no listener to wake or to reconnect.
//
// The first fact carries the atomic-write requirement: the event row and its
// outbox row land in one transaction or neither lands.
public class SqlServerOutboxProcessorTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlServerOutboxProcessorTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Append_writes_the_outbox_row_alongside_the_event_in_one_transaction()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = NewStore(connStr);
        var stream = ContractEnvelopes.NewStreamId();
        var payload = new ContractOrderPlaced(Guid.NewGuid(), 42m);
        var envelope = ContractEnvelopes.Build(stream, 1, payload);

        await store.AppendAsync(stream, 0, [envelope], CancellationToken.None);

        (await CountAsync(connStr, "event_store.events")).Should().Be(1);
        (await CountAsync(connStr, "event_store.outbox")).Should().Be(1);

        // The outbox row is self-describing: it carries the position threaded out of the events
        // INSERT, so the processor never joins back into event_store.events.
        var rows = await ReadOutboxAsync(connStr);
        rows.Should().HaveCount(1);
        rows[0].EventId.Should().Be(envelope.EventId);
        rows[0].EventType.Should().Be(nameof(ContractOrderPlaced));
        rows[0].GlobalPosition.Should().BeGreaterThan(0);
        rows[0].AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task A_failed_append_leaves_no_outbox_row_behind()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = NewStore(connStr);
        var stream = ContractEnvelopes.NewStreamId();
        await store.AppendAsync(stream, 0,
            [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
            CancellationToken.None);

        // A stale expected version: the events INSERT violates uq_events_stream_version and the
        // transaction rolls back. Atomicity means the outbox row it would have written dies too.
        var act = async () => await store.AppendAsync(stream, 0,
            [ContractEnvelopes.Build(stream, 1, new ContractOrderNoted("stale"))],
            CancellationToken.None);
        await act.Should().ThrowAsync<ConcurrencyException>();

        (await CountAsync(connStr, "event_store.events")).Should().Be(1);
        (await CountAsync(connStr, "event_store.outbox")).Should().Be(1);
    }

    [Fact]
    public async Task Drains_a_pending_row_and_marks_it_sent()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await AppendOneAsync(connStr);
        var dispatcher = new SqlServerRecordingDispatcher();

        var processed = await NewProcessor(connStr, dispatcher).ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        dispatcher.Received.Should().HaveCount(1);
        dispatcher.Received[0].GlobalPosition.Should().BeGreaterThan(0);
        (await ReadOutboxAsync(connStr))[0].SentUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task An_empty_outbox_returns_zero_and_does_not_call_the_dispatcher()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var dispatcher = new SqlServerRecordingDispatcher();

        var processed = await NewProcessor(connStr, dispatcher).ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(0);
        dispatcher.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_dispatch_increments_the_attempt_count_and_schedules_the_next_attempt()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await AppendOneAsync(connStr);
        var dispatcher = new SqlServerRecordingDispatcher
        {
            Evaluate = (_, _) => Task.FromResult<Exception?>(new InvalidOperationException("boom")),
        };

        await NewProcessor(connStr, dispatcher).ProcessBatchAsync(CancellationToken.None);

        var row = (await ReadOutboxAsync(connStr))[0];
        row.SentUtc.Should().BeNull();
        row.AttemptCount.Should().Be(1);
        row.NextAttemptAt.Should().NotBeNull();
        row.LastError.Should().Contain("boom");
    }

    [Fact]
    public async Task A_row_with_a_future_next_attempt_is_skipped()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await AppendOneAsync(connStr);
        var failing = new SqlServerRecordingDispatcher
        {
            Evaluate = (_, _) => Task.FromResult<Exception?>(new InvalidOperationException("boom")),
        };
        await NewProcessor(connStr, failing).ProcessBatchAsync(CancellationToken.None);

        // The backoff pushed next_attempt_at into the future, so the claim predicate excludes it.
        var second = new SqlServerRecordingDispatcher();
        var processed = await NewProcessor(connStr, second).ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(0);
        second.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Exceeding_max_attempts_quarantines_the_row_and_preserves_its_attempt_count()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await AppendOneAsync(connStr);
        var dispatcher = new SqlServerRecordingDispatcher
        {
            Evaluate = (_, _) => Task.FromResult<Exception?>(new InvalidOperationException("poison")),
        };

        // MaxAttempts of 1 quarantines on the first failure.
        await NewProcessor(connStr, dispatcher, maxAttempts: 1)
            .ProcessBatchAsync(CancellationToken.None);

        (await CountAsync(connStr, "event_store.outbox")).Should().Be(0);
        var quarantined = await ReadQuarantineAsync(connStr);
        quarantined.Should().HaveCount(1);
        quarantined[0].AttemptCount.Should().Be(1);
        quarantined[0].FinalError.Should().Contain("poison");
    }

    [Fact]
    public async Task A_success_after_a_prior_failure_marks_sent_and_keeps_the_attempt_count()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await AppendOneAsync(connStr);
        var failing = new SqlServerRecordingDispatcher
        {
            Evaluate = (_, _) => Task.FromResult<Exception?>(new InvalidOperationException("boom")),
        };
        await NewProcessor(connStr, failing).ProcessBatchAsync(CancellationToken.None);

        // Clear the backoff so the row is claimable again, then let it succeed.
        await ExecuteAsync(connStr, "UPDATE event_store.outbox SET next_attempt_at = NULL");
        await NewProcessor(connStr, new SqlServerRecordingDispatcher())
            .ProcessBatchAsync(CancellationToken.None);

        var row = (await ReadOutboxAsync(connStr))[0];
        row.SentUtc.Should().NotBeNull();
        row.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task Dispatches_in_fifo_order_within_a_batch()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = NewStore(connStr);
        var notes = new[] { "first", "second", "third" };
        foreach (var note in notes)
        {
            var stream = ContractEnvelopes.NewStreamId();
            await store.AppendAsync(stream, 0,
                [ContractEnvelopes.Build(stream, 1, new ContractOrderNoted(note))],
                CancellationToken.None);
        }

        var dispatcher = new SqlServerRecordingDispatcher();
        await NewProcessor(connStr, dispatcher).ProcessBatchAsync(CancellationToken.None);

        dispatcher.Received.Select(m => ((ContractOrderNoted)m.Event).Note).Should().Equal(notes);
        dispatcher.Received.Select(m => m.OutboxId).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Concurrent_claimants_take_disjoint_rows_and_neither_blocks()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = NewStore(connStr);
        for (var i = 0; i < 4; i++)
        {
            var stream = ContractEnvelopes.NewStreamId();
            await store.AppendAsync(stream, 0,
                [ContractEnvelopes.Build(stream, 1, new ContractOrderNoted($"m{i}"))],
                CancellationToken.None);
        }

        // A holds its batch open, so its rows are locked. B must step over them under READPAST
        // rather than block on them, and must not see them.
        await using var connA = new SqlConnection(connStr);
        await connA.OpenAsync();
        await using var txA = (SqlTransaction)await connA.BeginTransactionAsync();
        var claimedByA = await ClaimAsync(connA, txA, 2);

        var second = new SqlServerRecordingDispatcher();
        var processedByB = await NewProcessor(connStr, second, batchSize: 4)
            .ProcessBatchAsync(CancellationToken.None);

        processedByB.Should().Be(2);
        second.Received.Select(m => m.OutboxId).Should().NotIntersectWith(claimedByA);
        await txA.RollbackAsync();
    }

    [Fact]
    public async Task A_rolled_back_claimant_leaves_its_rows_claimable_with_no_cleanup()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = NewStore(connStr);
        for (var i = 0; i < 2; i++)
        {
            var stream = ContractEnvelopes.NewStreamId();
            await store.AppendAsync(stream, 0,
                [ContractEnvelopes.Build(stream, 1, new ContractOrderNoted($"m{i}"))],
                CancellationToken.None);
        }

        // Crash simulation. The row lock IS the claim, so a claimant that dies without committing
        // releases its rows when the engine releases its locks. No in-flight column, no reaper.
        await using (var connA = new SqlConnection(connStr))
        {
            await connA.OpenAsync();
            await using var txA = (SqlTransaction)await connA.BeginTransactionAsync();
            (await ClaimAsync(connA, txA, 2)).Should().HaveCount(2);
            await txA.RollbackAsync();
        }

        var dispatcher = new SqlServerRecordingDispatcher();
        var processed = await NewProcessor(connStr, dispatcher).ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(2);
        dispatcher.Received.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_subscriber_failure_never_loses_the_event_from_the_outbox()
    {
        // The outbox-durability requirement. The row survives a failing subscriber, stays unsent, and is
        // still there to be dispatched once the subscriber recovers.
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await AppendOneAsync(connStr);

        var failing = new SqlServerRecordingDispatcher
        {
            Evaluate = (_, _) => Task.FromResult<Exception?>(new InvalidOperationException("down")),
        };
        await NewProcessor(connStr, failing).ProcessBatchAsync(CancellationToken.None);

        (await CountAsync(connStr, "event_store.outbox")).Should().Be(1);
        (await ReadOutboxAsync(connStr))[0].SentUtc.Should().BeNull();

        await ExecuteAsync(connStr, "UPDATE event_store.outbox SET next_attempt_at = NULL");
        var recovered = new SqlServerRecordingDispatcher();
        await NewProcessor(connStr, recovered).ProcessBatchAsync(CancellationToken.None);

        recovered.Received.Should().HaveCount(1);
        (await ReadOutboxAsync(connStr))[0].SentUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Dispatched_message_carries_the_stored_event_version()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await AppendOneAsync(connStr);
        var dispatcher = new SqlServerRecordingDispatcher();

        await NewProcessor(connStr, dispatcher).ProcessBatchAsync(CancellationToken.None);

        dispatcher.Received.Should().ContainSingle();
        dispatcher.Received[0].EventVersion.Should().Be(1);
    }

    // A version-1 outbox row, dispatched. Green-on-write characterization pinning V2a's threading of
    // the V1 pipeline through the SQL Server outbox dispatch path; the mechanism was RED-proven at the
    // V1 unit level (EventUpcasterPipelineTests) and the V2a composition level
    // (EventUpcasterPipelineCompositionTests). The row is seeded at event_version 1 under the
    // terminal's storage name, and the processor lifts it to the current shape.
    [Fact]
    public async Task Dispatched_message_lifts_a_version_one_row_to_the_current_shape()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var itemId = Guid.NewGuid();
        await SeedVersionOneOutboxRowAsync(
            connStr, ContractEnvelopes.NewStreamId(), new ContractItemArchivedV1(itemId));
        var dispatcher = new SqlServerRecordingDispatcher();
        var pipeline = new EventUpcasterPipeline(NewRegistry(), [new ContractItemArchivedV1ToV2()]);

        await NewProcessor(connStr, dispatcher, pipeline: pipeline)
            .ProcessBatchAsync(CancellationToken.None);

        dispatcher.Received.Should().ContainSingle();
        var upcast = dispatcher.Received[0].Event.Should().BeOfType<ContractItemArchived>().Which;
        upcast.ItemId.Should().Be(itemId);
        upcast.Revision.Should().Be(1);
        dispatcher.Received[0].EventVersion.Should().Be(2);
    }

    private static SqlServerEventStore NewStore(string connStr)
        => new(
            new SqlServerConnectionFactory(connStr),
            NewRegistry(),
            new ProcessManagerEventTypeRegistry(),
            SqlServerContractBackend.CreateJsonOptions(),
            new EventUpcasterPipeline(NewRegistry(), []));

    private static EventTypeRegistry NewRegistry()
    {
        var registry = new EventTypeRegistry();
        foreach (var eventType in ContractEventTypes.DomainEvents)
        {
            registry.Register(eventType);
        }
        return registry;
    }

    private static SqlServerOutboxProcessor NewProcessor(
        string connStr,
        IMessageDispatcher dispatcher,
        int maxAttempts = 10,
        int batchSize = 100,
        EventUpcasterPipeline? pipeline = null)
        => new(
            new SqlServerConnectionFactory(connStr),
            dispatcher,
            NewRegistry(),
            SqlServerContractBackend.CreateJsonOptions(),
            new SqlServerOutboxRetryPolicy(),
            Options.Create(new SqlServerOutboxProcessorOptions
            {
                MaxAttempts = maxAttempts,
                BatchSize = batchSize,
                Jitter = () => 1.0,
            }),
            NullLogger<SqlServerOutboxProcessor>.Instance,
            pipeline ?? new EventUpcasterPipeline(NewRegistry(), []));

    private static async Task AppendOneAsync(string connStr)
    {
        var stream = ContractEnvelopes.NewStreamId();
        await NewStore(connStr).AppendAsync(stream, 0,
            [ContractEnvelopes.Build(stream, 1, new ContractOrderPlaced(Guid.NewGuid(), 1m))],
            CancellationToken.None);
    }

    // A version-1 outbox row, planted through a throwaway store whose registry maps the V1 shape onto
    // the terminal's stable storage name, so the adapter's own append writes event_version 1 with the
    // V1 payload, the way an older revision of the code left it.
    private static async Task SeedVersionOneOutboxRowAsync(
        string connStr, StreamId stream, IDomainEvent versionOnePayload)
    {
        var seedRegistry = new EventTypeRegistry().Register(
            versionOnePayload.GetType(), nameof(ContractItemArchived));
        var seedStore = new SqlServerEventStore(
            new SqlServerConnectionFactory(connStr),
            seedRegistry,
            new ProcessManagerEventTypeRegistry(),
            SqlServerContractBackend.CreateJsonOptions(),
            new EventUpcasterPipeline(seedRegistry, []));
        await seedStore.AppendAsync(
            stream, 0, [ContractEnvelopes.Build(stream, 1, versionOnePayload)], CancellationToken.None);
    }

    // The processor's own claim SQL, run by hand so a test can hold rows locked.
    private static async Task<List<long>> ClaimAsync(SqlConnection conn, SqlTransaction tx, int top)
    {
        await using var cmd = new SqlCommand(
            "SELECT TOP (@n) outbox_id FROM event_store.outbox WITH (UPDLOCK, READPAST, ROWLOCK) " +
            "WHERE sent_utc IS NULL ORDER BY outbox_id",
            conn, tx);
        cmd.Parameters.Add("@n", System.Data.SqlDbType.Int).Value = top;

        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static async Task ExecuteAsync(string connStr, string sql)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(string connStr, string table)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM {table}", conn);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<OutboxRow>> ReadOutboxAsync(string connStr)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT outbox_id, event_id, event_type, global_position, attempt_count, " +
            "sent_utc, next_attempt_at, last_error " +
            "FROM event_store.outbox ORDER BY outbox_id", conn);

        var rows = new List<OutboxRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5),
                reader.IsDBNull(6) ? null : reader.GetDateTimeOffset(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return rows;
    }

    private static async Task<List<QuarantineRow>> ReadQuarantineAsync(string connStr)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT outbox_id, event_id, attempt_count, final_error " +
            "FROM event_store.outbox_quarantine ORDER BY quarantine_id", conn);

        var rows = new List<QuarantineRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new QuarantineRow(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3)));
        }
        return rows;
    }

    private sealed record OutboxRow(
        long OutboxId,
        Guid EventId,
        string EventType,
        long GlobalPosition,
        int AttemptCount,
        DateTimeOffset? SentUtc,
        DateTimeOffset? NextAttemptAt,
        string? LastError);

    private sealed record QuarantineRow(
        long OutboxId, Guid EventId, int AttemptCount, string FinalError);
}

// Hand-rolled IMessageDispatcher test double. Records every message it receives. Evaluate is the
// per-message hook a test uses to fail the dispatch. Duplicated from the PostgreSQL adapter's
// RecordingDispatcher per ADR 0004: it lives in that adapter's test namespace and the two do not
// reach into each other.
internal sealed class SqlServerRecordingDispatcher : IMessageDispatcher
{
    public List<OutboxMessage> Received { get; } = [];

    public Func<OutboxMessage, CancellationToken, Task<Exception?>>? Evaluate { get; set; }

    public async Task DispatchAsync(OutboxMessage message, CancellationToken ct)
    {
        Received.Add(message);
        if (Evaluate is null)
        {
            return;
        }
        var ex = await Evaluate(message, ct);
        if (ex is not null)
        {
            throw ex;
        }
    }
}
