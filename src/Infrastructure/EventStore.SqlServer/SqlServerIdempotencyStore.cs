using System.Data;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Data.SqlClient;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Hand-rolled SQL Server implementation of IIdempotencyStore (ADR 0016). ExistsAsync is the eager
// check; TryRecordAsync is the lazy fallback that decides a race.
//
// Duplicated from the PostgreSQL adapter per ADR 0004, which is not a formality here: the two
// implementations share a contract and share no SQL, because the insert-if-absent idiom is
// engine-specific and this is the one place a naive translation would be silently wrong.
public sealed class SqlServerIdempotencyStore : IIdempotencyStore
{
    private readonly ISqlServerConnectionFactory _factory;

    public SqlServerIdempotencyStore(ISqlServerConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<bool> ExistsAsync(TenantId tenant, string idempotencyKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT 1 FROM event_store.command_idempotency " +
            "WHERE tenant_id = @tenant AND idempotency_key = @key",
            connection);
        AddUuid(cmd, "tenant", tenant.Value);
        AddKey(cmd, "key", idempotencyKey);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null and not DBNull;
    }

    // The counterpart of PostgreSQL's INSERT ... ON CONFLICT DO NOTHING, and the reason this class
    // exists rather than a shared one.
    //
    // T-SQL has no ON CONFLICT. The obvious translations are all wrong in a way that only shows up
    // under load:
    //
    //   IF NOT EXISTS (...) INSERT ...   is check-then-act. Two dispatches of the same key can
    //                                    both pass the check, and the second insert raises 2627.
    //   INSERT, catch 2627               works, but makes an expected race an exception, which the
    //                                    PostgreSQL side deliberately avoids.
    //   MERGE                            is not concurrency-safe here without the same hints below.
    //
    // INSERT ... SELECT ... WHERE NOT EXISTS with UPDLOCK and HOLDLOCK is the idiom that is atomic.
    // HOLDLOCK takes a range lock on the key even when no row exists, so a second transaction
    // testing the same key blocks until the first commits and then sees the row. The engine decides
    // the race, not the application, and the loser learns it lost from a rows-affected count of
    // zero rather than from an exception. Same observable contract as the PostgreSQL store: true on
    // a first write, false on a race-loss, no throw either way.
    public async Task<bool> TryRecordAsync(
        TenantId tenant, string idempotencyKey, string commandType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(
            "INSERT INTO event_store.command_idempotency (tenant_id, idempotency_key, command_type) " +
            "SELECT @tenant, @key, @command_type " +
            "WHERE NOT EXISTS ( " +
            "    SELECT 1 FROM event_store.command_idempotency WITH (UPDLOCK, HOLDLOCK) " +
            "    WHERE tenant_id = @tenant AND idempotency_key = @key)",
            connection);
        AddUuid(cmd, "tenant", tenant.Value);
        AddKey(cmd, "key", idempotencyKey);
        AddKey(cmd, "command_type", commandType);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows == 1;
    }

    private static void AddUuid(SqlCommand cmd, string name, Guid value)
        => cmd.Parameters.Add("@" + name, SqlDbType.UniqueIdentifier).Value = value;

    // VarChar to match the column, which keeps the primary-key seek on (tenant_id,
    // idempotency_key). An NVARCHAR parameter against a VARCHAR key column converts the column and
    // costs the seek, and this is the hottest lookup on the command path.
    private static void AddKey(SqlCommand cmd, string name, string value)
        => cmd.Parameters.Add("@" + name, SqlDbType.VarChar, 200).Value = value;
}
