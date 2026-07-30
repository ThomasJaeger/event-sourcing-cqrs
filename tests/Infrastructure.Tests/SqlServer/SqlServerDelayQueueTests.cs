using System.Data;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// The SQL Server twins of every PostgresDelayQueue_Tests fact, at the same seams.
public class SqlServerDelayQueueTests : IClassFixture<SqlServerFixture>
{
    private static readonly DateTimeOffset FireAt = new(2026, 5, 21, 12, 30, 0, TimeSpan.Zero);
    private static readonly SystemActor Pm = SystemActors.OrderFulfillment;

    private readonly SqlServerFixture _fixture;

    public SqlServerDelayQueueTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ScheduleAsync_persists_a_self_describing_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var queue = NewQueue(connStr);
        var stream = NewPmStream();
        var correlationId = Guid.NewGuid();
        var causationId = Guid.NewGuid();

        await queue.ScheduleAsync(
            new TestTimeout(Guid.NewGuid()), FireAt, stream, "await-payment",
            CausingEvent(correlationId, causationId), Pm, "key-1", CancellationToken.None);

        var rows = await ReadRowsAsync(connStr, stream.Value, "await-payment");
        rows.Should().ContainSingle();
        var row = rows[0];
        row.CommandType.Should().Be(nameof(TestTimeout));
        row.CorrelationId.Should().Be(correlationId);
        row.CausationId.Should().Be(causationId);
        row.ActorId.Should().Be(Pm.Id);
        row.ServiceName.Should().Be(Pm.ServiceName);
        row.IdempotencyKey.Should().Be("key-1");
        row.DispatchedAtUtc.Should().BeNull();
        row.CancelledAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task CancelAsync_cancels_a_pending_row_and_returns_true()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var queue = NewQueue(connStr);
        var stream = NewPmStream();
        await ScheduleAsync(queue, stream, "await-payment", "key-1");

        (await queue.CancelAsync(stream, "await-payment", "paid", CancellationToken.None))
            .Should().BeTrue();

        var row = (await ReadRowsAsync(connStr, stream.Value, "await-payment"))[0];
        row.CancelledAtUtc.Should().NotBeNull();
        row.CancellationReason.Should().Be("paid");
    }

    [Fact]
    public async Task CancelAsync_cancels_all_pending_rows_for_the_same_stream_and_step()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var queue = NewQueue(connStr);
        var stream = NewPmStream();
        await ScheduleAsync(queue, stream, "await-payment", "key-1");
        await ScheduleAsync(queue, stream, "await-payment", "key-2");

        (await queue.CancelAsync(stream, "await-payment", "paid", CancellationToken.None))
            .Should().BeTrue();

        var rows = await ReadRowsAsync(connStr, stream.Value, "await-payment");
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.CancelledAtUtc != null);
    }

    [Fact]
    public async Task CancelAsync_returns_false_when_no_pending_row_matches()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var queue = NewQueue(connStr);

        (await queue.CancelAsync(NewPmStream(), "await-payment", "paid", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task CancelAsync_is_a_no_op_on_an_already_cancelled_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var queue = NewQueue(connStr);
        var stream = NewPmStream();
        await ScheduleAsync(queue, stream, "await-payment", "key-1");
        await queue.CancelAsync(stream, "await-payment", "paid", CancellationToken.None);

        (await queue.CancelAsync(stream, "await-payment", "paid again", CancellationToken.None))
            .Should().BeFalse();

        var row = (await ReadRowsAsync(connStr, stream.Value, "await-payment"))[0];
        row.CancellationReason.Should().Be("paid");
    }

    [Fact]
    public async Task CancelAsync_does_not_cancel_an_already_dispatched_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var queue = NewQueue(connStr);
        var stream = NewPmStream();
        await ScheduleAsync(queue, stream, "await-payment", "key-1");
        await ExecuteAsync(connStr,
            "UPDATE event_store.delayed_commands SET dispatched_at_utc = SYSUTCDATETIME()");

        (await queue.CancelAsync(stream, "await-payment", "paid", CancellationToken.None))
            .Should().BeFalse();

        (await ReadRowsAsync(connStr, stream.Value, "await-payment"))[0]
            .CancelledAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleAsync_rejects_a_blank_step()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var queue = NewQueue(connStr);

        var act = async () => await queue.ScheduleAsync(
            new TestTimeout(Guid.NewGuid()), FireAt, NewPmStream(), "  ",
            CausingEvent(Guid.NewGuid(), Guid.NewGuid()), Pm, "key-1", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ScheduleAsync_rejects_a_blank_idempotency_key()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var queue = NewQueue(connStr);

        var act = async () => await queue.ScheduleAsync(
            new TestTimeout(Guid.NewGuid()), FireAt, NewPmStream(), "await-payment",
            CausingEvent(Guid.NewGuid(), Guid.NewGuid()), Pm, "  ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Schedule_persists_the_causing_event_tenant_on_the_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var queue = NewQueue(connStr);
        var stream = NewPmStream();
        var tenant = TenantId.From(Guid.NewGuid());

        await queue.ScheduleAsync(
            new TestTimeout(Guid.NewGuid()), FireAt, stream, "await-payment",
            CausingEvent(Guid.NewGuid(), Guid.NewGuid(), tenant), Pm, "key-1",
            CancellationToken.None);

        (await ReadRowsAsync(connStr, stream.Value, "await-payment"))[0]
            .Tenant.Should().Be(tenant);
    }

    private static SqlServerDelayQueue NewQueue(string connStr)
        => new(
            new SqlServerConnectionFactory(connStr),
            new CommandTypeRegistry().Register<TestTimeout>(),
            SqlServerContractBackend.CreateJsonOptions());

    private static StreamId NewPmStream()
        => StreamId.Parse($"pm-order-fulfillment:{Guid.NewGuid():N}");

    private static EventMetadata CausingEvent(Guid correlationId, Guid eventId, TenantId? tenant = null)
        => new(
            EventId: eventId,
            CorrelationId: correlationId,
            CausationId: Guid.NewGuid(),
            ActorId: Guid.NewGuid(),
            Source: "Sales",
            OccurredUtc: new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
            Tenant: tenant ?? WellKnownTenants.Default);

    private static Task ScheduleAsync(
        SqlServerDelayQueue queue, StreamId stream, string step, string idempotencyKey)
        => queue.ScheduleAsync(
            new TestTimeout(Guid.NewGuid()), FireAt, stream, step,
            CausingEvent(Guid.NewGuid(), Guid.NewGuid()), Pm, idempotencyKey, CancellationToken.None);

    private static async Task ExecuteAsync(string connStr, string sql)
    {
        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<DelayedRow>> ReadRowsAsync(
        string connStr, string streamId, string step)
    {
        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT command_type, command_payload, correlation_id, causation_id, actor_id, " +
            "service_name, idempotency_key, dispatched_at_utc, cancelled_at_utc, " +
            "cancellation_reason, tenant_id " +
            "FROM event_store.delayed_commands " +
            "WHERE scheduled_by_stream_id = @stream_id AND scheduled_by_step = @step " +
            "ORDER BY delayed_command_id",
            connection);
        cmd.Parameters.Add("@stream_id", SqlDbType.VarChar, 200).Value = streamId;
        cmd.Parameters.Add("@step", SqlDbType.VarChar, 200).Value = step;

        var rows = new List<DelayedRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new DelayedRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetDateTimeOffset(7),
                reader.IsDBNull(8) ? null : reader.GetDateTimeOffset(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                TenantId.From(reader.GetGuid(10))));
        }
        return rows;
    }

    private sealed record DelayedRow(
        string CommandType,
        string CommandPayload,
        Guid CorrelationId,
        Guid CausationId,
        Guid ActorId,
        string ServiceName,
        string IdempotencyKey,
        DateTimeOffset? DispatchedAtUtc,
        DateTimeOffset? CancelledAtUtc,
        string? CancellationReason,
        TenantId Tenant);

    internal sealed record TestTimeout(Guid OrderId) : ICommand;
}
