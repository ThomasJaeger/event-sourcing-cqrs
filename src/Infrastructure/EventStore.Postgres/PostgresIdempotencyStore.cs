using EventSourcingCqrs.Domain.Abstractions;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Hand-rolled PostgreSQL implementation of IIdempotencyStore (ADR 0016).
// ExistsAsync is the eager check. TryRecordAsync inserts with ON CONFLICT DO
// NOTHING and reads the rows-affected count as the lazy fallback, so a
// race-lost insert reports false without raising and catching a
// unique-violation. Lives here with the other Postgres adapters because it
// consumes the same INpgsqlConnectionFactory; the table sits in the event_store
// schema alongside events and outbox.
public sealed class PostgresIdempotencyStore : IIdempotencyStore
{
    private readonly INpgsqlConnectionFactory _factory;

    public PostgresIdempotencyStore(INpgsqlConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<bool> ExistsAsync(TenantId tenant, string idempotencyKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM event_store.command_idempotency " +
            "WHERE tenant_id = @tenant AND idempotency_key = @key";
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant.Value);
        cmd.Parameters.AddWithValue("key", NpgsqlDbType.Text, idempotencyKey);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null and not DBNull;
    }

    public async Task<bool> TryRecordAsync(
        TenantId tenant, string idempotencyKey, string commandType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.command_idempotency (tenant_id, idempotency_key, command_type) " +
            "VALUES (@tenant, @key, @command_type) " +
            "ON CONFLICT (tenant_id, idempotency_key) DO NOTHING";
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant.Value);
        cmd.Parameters.AddWithValue("key", NpgsqlDbType.Text, idempotencyKey);
        cmd.Parameters.AddWithValue("command_type", NpgsqlDbType.Text, commandType);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows == 1;
    }
}
