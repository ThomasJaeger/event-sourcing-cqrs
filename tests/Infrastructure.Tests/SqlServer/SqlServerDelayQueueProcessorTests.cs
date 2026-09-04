using System.Data;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// The SQL Server twins of every DelayQueueProcessorTests fact except the notification one, which
// has nothing to test here: no LISTEN/NOTIFY, no listener, and ADR 0045's engine mapping, which leaves engine-native triggers unused, scopes this engine to
// polling, so the idle timer is the whole wake path.
public class SqlServerDelayQueueProcessorTests : IClassFixture<SqlServerFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly SystemActor Pm = SystemActors.OrderFulfillment;

    private readonly SqlServerFixture _fixture;

    public SqlServerDelayQueueProcessorTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Dispatches_a_due_row_and_marks_it_dispatched()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await ScheduleAsync(connStr, Now.AddMinutes(-1));
        var bus = new RecordingCausedCommandBus();

        var processed = await NewProcessor(connStr, bus).ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        bus.Sent.Should().HaveCount(1);
        (await ReadRowsAsync(connStr))[0].DispatchedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task An_empty_queue_returns_zero_and_does_not_dispatch()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var bus = new RecordingCausedCommandBus();

        (await NewProcessor(connStr, bus).ProcessBatchAsync(CancellationToken.None)).Should().Be(0);
        bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_future_dated_row_is_not_due_and_is_not_dispatched()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await ScheduleAsync(connStr, Now.AddHours(1));
        var bus = new RecordingCausedCommandBus();

        (await NewProcessor(connStr, bus).ProcessBatchAsync(CancellationToken.None)).Should().Be(0);
        bus.Sent.Should().BeEmpty();
        (await ReadRowsAsync(connStr))[0].DispatchedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_past_dated_row_is_dispatched_immediately()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await ScheduleAsync(connStr, Now.AddDays(-1));
        var bus = new RecordingCausedCommandBus();

        (await NewProcessor(connStr, bus).ProcessBatchAsync(CancellationToken.None)).Should().Be(1);
        bus.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_failed_dispatch_increments_the_attempt_count_and_schedules_the_next_attempt()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await ScheduleAsync(connStr, Now.AddMinutes(-1));
        var bus = new RecordingCausedCommandBus { Throw = new InvalidOperationException("boom") };

        await NewProcessor(connStr, bus).ProcessBatchAsync(CancellationToken.None);

        var row = (await ReadRowsAsync(connStr))[0];
        row.DispatchedAtUtc.Should().BeNull();
        row.AttemptCount.Should().Be(1);
        row.NextAttemptAt.Should().NotBeNull();
        row.LastError.Should().Contain("boom");
    }

    [Fact]
    public async Task Exceeding_max_attempts_quarantines_the_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await ScheduleAsync(connStr, Now.AddMinutes(-1));
        var bus = new RecordingCausedCommandBus { Throw = new InvalidOperationException("poison") };

        await NewProcessor(connStr, bus, maxAttempts: 1).ProcessBatchAsync(CancellationToken.None);

        (await CountAsync(connStr, "event_store.delayed_commands")).Should().Be(0);
        var quarantined = await ReadQuarantineAsync(connStr);
        quarantined.Should().HaveCount(1);
        quarantined[0].AttemptCount.Should().Be(1);
        quarantined[0].FinalError.Should().Contain("poison");
    }

    [Fact]
    public async Task A_cancelled_row_is_not_claimed()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await ScheduleAsync(connStr, Now.AddMinutes(-1));
        await ExecuteAsync(connStr,
            "UPDATE event_store.delayed_commands SET cancelled_at_utc = SYSUTCDATETIME()");
        var bus = new RecordingCausedCommandBus();

        (await NewProcessor(connStr, bus).ProcessBatchAsync(CancellationToken.None)).Should().Be(0);
        bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task An_already_dispatched_row_is_not_claimed()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await ScheduleAsync(connStr, Now.AddMinutes(-1));
        await ExecuteAsync(connStr,
            "UPDATE event_store.delayed_commands SET dispatched_at_utc = SYSUTCDATETIME()");
        var bus = new RecordingCausedCommandBus();

        (await NewProcessor(connStr, bus).ProcessBatchAsync(CancellationToken.None)).Should().Be(0);
        bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_processors_take_disjoint_rows_and_neither_blocks()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        for (var i = 0; i < 4; i++)
        {
            await ScheduleAsync(connStr, Now.AddMinutes(-1));
        }

        // A holds its claim open, so its rows are locked. B must step over them under READPAST
        // rather than block, and must not see them.
        await using var connA = new SqlConnection(connStr);
        await connA.OpenAsync();
        await using var txA = (SqlTransaction)await connA.BeginTransactionAsync();
        var claimedByA = await ClaimAsync(connA, txA, 2);
        claimedByA.Should().HaveCount(2);

        var bus = new RecordingCausedCommandBus();
        var processedByB = await NewProcessor(connStr, bus).ProcessBatchAsync(CancellationToken.None);

        processedByB.Should().Be(2);
        await txA.RollbackAsync();

        // A's rows were never dispatched and stay claimable: the row lock is the claim, and a
        // claimant that dies releases them with no cleanup code.
        var stillPending = (await ReadRowsAsync(connStr)).Where(r => r.DispatchedAtUtc is null);
        stillPending.Select(r => r.DelayedCommandId).Should().BeEquivalentTo(claimedByA);
    }

    [Fact]
    public async Task Reconstructs_the_dispatch_context_from_the_row_columns()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var correlationId = Guid.NewGuid();
        var causationId = Guid.NewGuid();
        await ScheduleAsync(connStr, Now.AddMinutes(-1), correlationId, causationId, key: "key-7");
        var bus = new RecordingCausedCommandBus();

        await NewProcessor(connStr, bus).ProcessBatchAsync(CancellationToken.None);

        // ADR 0014: the resurfaced command carries the scheduling event's correlation and causation
        // forward, so the workflow stays one trace across the timeout boundary.
        var sent = bus.Sent.Should().ContainSingle().Subject;
        sent.Command.Should().BeOfType<SqlServerDelayQueueTests.TestTimeout>();
        sent.CausingMetadata.CorrelationId.Should().Be(correlationId);
        sent.CausingMetadata.EventId.Should().Be(causationId);
        sent.Actor.Id.Should().Be(Pm.Id);
        sent.Actor.ServiceName.Should().Be(Pm.ServiceName);
        sent.IdempotencyKey.Should().Be("key-7");
    }

    [Fact]
    public async Task Resurfaces_a_due_command_in_its_stored_tenant()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        await ScheduleAsync(connStr, Now.AddMinutes(-1), tenant: tenant);
        var bus = new RecordingCausedCommandBus();

        await NewProcessor(connStr, bus).ProcessBatchAsync(CancellationToken.None);

        bus.Sent.Should().ContainSingle().Which.CausingMetadata.Tenant.Should().Be(tenant);
    }

    private static SqlServerDelayQueue NewQueue(string connStr)
        => new(
            new SqlServerConnectionFactory(connStr),
            new CommandTypeRegistry().Register<SqlServerDelayQueueTests.TestTimeout>(),
            SqlServerContractBackend.CreateJsonOptions());

    private static SqlServerDelayQueueProcessor NewProcessor(
        string connStr, ICausedCommandBus bus, int maxAttempts = 10)
        => new(
            new SqlServerConnectionFactory(connStr),
            bus,
            new CommandTypeRegistry().Register<SqlServerDelayQueueTests.TestTimeout>(),
            SqlServerContractBackend.CreateJsonOptions(),
            new SqlServerDelayQueueRetryPolicy(),
            Options.Create(new SqlServerDelayQueueProcessorOptions
            {
                MaxAttempts = maxAttempts,
                TimeProvider = new FixedTimeProvider(Now),
                Jitter = () => 1.0,
            }),
            NullLogger<SqlServerDelayQueueProcessor>.Instance);

    private static Task ScheduleAsync(
        string connStr,
        DateTimeOffset fireAt,
        Guid? correlationId = null,
        Guid? causationId = null,
        TenantId? tenant = null,
        string key = "key-1")
        => NewQueue(connStr).ScheduleAsync(
            new SqlServerDelayQueueTests.TestTimeout(Guid.NewGuid()),
            fireAt,
            StreamId.Parse($"pm-order-fulfillment:{Guid.NewGuid():N}"),
            "await-payment",
            new EventMetadata(
                EventId: causationId ?? Guid.NewGuid(),
                CorrelationId: correlationId ?? Guid.NewGuid(),
                CausationId: Guid.NewGuid(),
                ActorId: Guid.NewGuid(),
                Source: "Sales",
                OccurredUtc: new DateTime(2026, 5, 21, 11, 0, 0, DateTimeKind.Utc),
                Tenant: tenant ?? WellKnownTenants.Default),
            Pm,
            key,
            CancellationToken.None);

    // The processor's own claim SQL, run by hand so a test can hold rows locked.
    private static async Task<List<long>> ClaimAsync(SqlConnection conn, SqlTransaction tx, int top)
    {
        await using var cmd = new SqlCommand(
            "SELECT TOP (@n) delayed_command_id " +
            "FROM event_store.delayed_commands WITH (UPDLOCK, READPAST, ROWLOCK) " +
            "WHERE dispatched_at_utc IS NULL AND cancelled_at_utc IS NULL " +
            "ORDER BY fire_at_utc, delayed_command_id",
            conn, tx);
        cmd.Parameters.Add("@n", SqlDbType.Int).Value = top;

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

    private static async Task<List<PendingRow>> ReadRowsAsync(string connStr)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT delayed_command_id, attempt_count, dispatched_at_utc, next_attempt_at, last_error " +
            "FROM event_store.delayed_commands ORDER BY delayed_command_id", conn);

        var rows = new List<PendingRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new PendingRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2),
                reader.IsDBNull(3) ? null : reader.GetDateTimeOffset(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return rows;
    }

    private static async Task<List<QuarantinedRow>> ReadQuarantineAsync(string connStr)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT delayed_command_id, attempt_count, final_error " +
            "FROM event_store.delayed_commands_quarantine ORDER BY quarantine_id", conn);

        var rows = new List<QuarantinedRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new QuarantinedRow(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2)));
        }
        return rows;
    }

    private sealed record PendingRow(
        long DelayedCommandId,
        int AttemptCount,
        DateTimeOffset? DispatchedAtUtc,
        DateTimeOffset? NextAttemptAt,
        string? LastError);

    private sealed record QuarantinedRow(long DelayedCommandId, int AttemptCount, string FinalError);

    // Freezes time so the due predicate and the backoff are deterministic.
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

// Hand-rolled ICausedCommandBus test double. Records every dispatch; Throw makes the dispatch fail.
internal sealed class RecordingCausedCommandBus : ICausedCommandBus
{
    public List<SentCommand> Sent { get; } = [];

    public Exception? Throw { get; init; }

    public Task SendAsync(
        ICommand command,
        EventMetadata causingEventMetadata,
        SystemActor actor,
        string? idempotencyKey,
        CancellationToken ct)
    {
        Sent.Add(new SentCommand(command, causingEventMetadata, actor, idempotencyKey));
        return Throw is not null ? Task.FromException(Throw) : Task.CompletedTask;
    }

    internal sealed record SentCommand(
        ICommand Command,
        EventMetadata CausingMetadata,
        SystemActor Actor,
        string? IdempotencyKey);
}
