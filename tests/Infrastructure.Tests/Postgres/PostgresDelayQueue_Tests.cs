using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class PostgresDelayQueue_Tests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    private static readonly DateTimeOffset FireAt = new(2026, 5, 21, 12, 30, 0, TimeSpan.Zero);
    private static readonly SystemActor Pm = SystemActors.OrderFulfillment;

    public PostgresDelayQueue_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ScheduleAsync_persists_a_self_describing_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var queue = NewQueue(dataSource);
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
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var queue = NewQueue(dataSource);
        var stream = NewPmStream();
        await ScheduleAsync(queue, stream, "await-payment", "key-1");

        var cancelled = await queue.CancelAsync(
            stream, "await-payment", "payment arrived", CancellationToken.None);

        cancelled.Should().BeTrue();
        var rows = await ReadRowsAsync(connStr, stream.Value, "await-payment");
        rows.Should().ContainSingle();
        rows[0].CancelledAtUtc.Should().NotBeNull();
        rows[0].CancellationReason.Should().Be("payment arrived");
    }

    [Fact]
    public async Task CancelAsync_cancels_all_pending_rows_for_the_same_stream_and_step()
    {
        // Two distinct timeouts scheduled for the same step (different keys).
        // CancelAsync matches on stream and step, not on the idempotency key, so
        // cancelling the step cancels both.
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var queue = NewQueue(dataSource);
        var stream = NewPmStream();
        await ScheduleAsync(queue, stream, "await-payment", "key-1");
        await ScheduleAsync(queue, stream, "await-payment", "key-2");

        var cancelled = await queue.CancelAsync(
            stream, "await-payment", "payment arrived", CancellationToken.None);

        cancelled.Should().BeTrue();
        var rows = await ReadRowsAsync(connStr, stream.Value, "await-payment");
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.CancelledAtUtc.Should().NotBeNull());
    }

    [Fact]
    public async Task CancelAsync_returns_false_when_no_pending_row_matches()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var queue = NewQueue(dataSource);

        var cancelled = await queue.CancelAsync(
            NewPmStream(), "await-payment", "nothing here", CancellationToken.None);

        cancelled.Should().BeFalse();
    }

    [Fact]
    public async Task CancelAsync_is_a_no_op_on_an_already_cancelled_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var queue = NewQueue(dataSource);
        var stream = NewPmStream();
        await ScheduleAsync(queue, stream, "await-payment", "key-1");
        await queue.CancelAsync(stream, "await-payment", "first", CancellationToken.None);

        var second = await queue.CancelAsync(
            stream, "await-payment", "second", CancellationToken.None);

        second.Should().BeFalse();
        var rows = await ReadRowsAsync(connStr, stream.Value, "await-payment");
        rows[0].CancellationReason.Should().Be("first");
    }

    [Fact]
    public async Task CancelAsync_does_not_cancel_an_already_dispatched_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var queue = NewQueue(dataSource);
        var stream = NewPmStream();
        await ScheduleAsync(queue, stream, "await-payment", "key-1");
        await MarkDispatchedAsync(connStr, stream.Value, "await-payment");

        var cancelled = await queue.CancelAsync(
            stream, "await-payment", "too late", CancellationToken.None);

        cancelled.Should().BeFalse();
        var rows = await ReadRowsAsync(connStr, stream.Value, "await-payment");
        rows[0].DispatchedAtUtc.Should().NotBeNull();
        rows[0].CancelledAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleAsync_rejects_a_blank_step()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var queue = NewQueue(dataSource);

        var act = async () => await queue.ScheduleAsync(
            new TestTimeout(Guid.NewGuid()), FireAt, NewPmStream(), "  ",
            CausingEvent(Guid.NewGuid(), Guid.NewGuid()), Pm, "key-1", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ScheduleAsync_rejects_a_blank_idempotency_key()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var queue = NewQueue(dataSource);

        var act = async () => await queue.ScheduleAsync(
            new TestTimeout(Guid.NewGuid()), FireAt, NewPmStream(), "await-payment",
            CausingEvent(Guid.NewGuid(), Guid.NewGuid()), Pm, "  ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static PostgresDelayQueue NewQueue(NpgsqlDataSource dataSource)
        => new(
            new NpgsqlConnectionFactory(dataSource),
            new CommandTypeRegistry().Register<TestTimeout>(),
            CreateJsonOptions());

    private static StreamId NewPmStream()
        => StreamId.Parse($"pm-order-fulfillment:{Guid.NewGuid():N}");

    private static EventMetadata CausingEvent(Guid correlationId, Guid eventId)
        => new(
            EventId: eventId,
            CorrelationId: correlationId,
            CausationId: Guid.NewGuid(),
            ActorId: Guid.NewGuid(),
            Source: "Sales",
            SchemaVersion: 1,
            OccurredUtc: new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc));

    private static Task ScheduleAsync(
        PostgresDelayQueue queue, StreamId stream, string step, string idempotencyKey)
        => queue.ScheduleAsync(
            new TestTimeout(Guid.NewGuid()), FireAt, stream, step,
            CausingEvent(Guid.NewGuid(), Guid.NewGuid()), Pm, idempotencyKey, CancellationToken.None);

    private static async Task<List<DelayedRow>> ReadRowsAsync(
        string connStr, string streamId, string step)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT command_type, command_payload, correlation_id, causation_id, actor_id, " +
            "service_name, idempotency_key, dispatched_at_utc, cancelled_at_utc, cancellation_reason " +
            "FROM event_store.delayed_commands " +
            "WHERE scheduled_by_stream_id = @stream_id AND scheduled_by_step = @step " +
            "ORDER BY delayed_command_id";
        cmd.Parameters.AddWithValue("stream_id", NpgsqlDbType.Text, streamId);
        cmd.Parameters.AddWithValue("step", NpgsqlDbType.Text, step);

        var rows = new List<DelayedRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new DelayedRow(
                CommandType: reader.GetString(0),
                CommandPayload: reader.GetString(1),
                CorrelationId: reader.GetGuid(2),
                CausationId: reader.GetGuid(3),
                ActorId: reader.GetGuid(4),
                ServiceName: reader.GetString(5),
                IdempotencyKey: reader.GetString(6),
                DispatchedAtUtc: reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                CancelledAtUtc: reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                CancellationReason: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return rows;
    }

    private static async Task MarkDispatchedAsync(string connStr, string streamId, string step)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "UPDATE event_store.delayed_commands SET dispatched_at_utc = now() " +
            "WHERE scheduled_by_stream_id = @stream_id AND scheduled_by_step = @step";
        cmd.Parameters.AddWithValue("stream_id", NpgsqlDbType.Text, streamId);
        cmd.Parameters.AddWithValue("step", NpgsqlDbType.Text, step);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record TestTimeout(Guid OrderId) : ICommand;

    private sealed record DelayedRow(
        string CommandType,
        string CommandPayload,
        Guid CorrelationId,
        Guid CausationId,
        Guid ActorId,
        string ServiceName,
        string IdempotencyKey,
        DateTime? DispatchedAtUtc,
        DateTime? CancelledAtUtc,
        string? CancellationReason);
}
