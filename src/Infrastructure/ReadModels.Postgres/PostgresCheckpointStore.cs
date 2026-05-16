using System.Data.Common;
using EventSourcingCqrs.Domain.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// PostgreSQL implementation of ICheckpointStore. A projection's checkpoint is
// one row in read_models.projection_checkpoints keyed by projection name.
//
// AdvanceAsync runs inside the caller's transaction so the checkpoint moves
// atomically with the read-model write the handler just made. The UPSERT uses
// GREATEST so the advance is idempotent: an at-least-once redelivery carrying
// an already-processed position leaves the stored position untouched.
public sealed class PostgresCheckpointStore : ICheckpointStore
{
    private readonly IReadModelConnectionFactory _factory;

    public PostgresCheckpointStore(IReadModelConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<long> GetPositionAsync(string projectionName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT position FROM read_models.projection_checkpoints " +
            "WHERE projection_name = @projection_name";
        cmd.Parameters.AddWithValue("projection_name", NpgsqlDbType.Text, projectionName);

        // No row: the projection has never checkpointed. 0 is the "from the
        // start" position; ReadAllAsync treats it as an exclusive lower bound,
        // so the first event read is global_position 1.
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0L : (long)result;
    }

    public async Task<long> GetPositionAsync(
        string projectionName,
        DbTransaction transaction,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentNullException.ThrowIfNull(transaction);

        // Same SELECT as the non-transactional overload, joined to the caller's
        // transaction so the read sees the handler's own uncommitted advance
        // (read-your-own-writes) and runs at the transaction's isolation level.
        var npgsqlTransaction = (NpgsqlTransaction)transaction;
        await using var cmd = npgsqlTransaction.Connection!.CreateCommand();
        cmd.Transaction = npgsqlTransaction;
        cmd.CommandText =
            "SELECT position FROM read_models.projection_checkpoints " +
            "WHERE projection_name = @projection_name";
        cmd.Parameters.AddWithValue("projection_name", NpgsqlDbType.Text, projectionName);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0L : (long)result;
    }

    public async Task AdvanceAsync(
        string projectionName,
        long position,
        DbTransaction transaction,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentNullException.ThrowIfNull(transaction);

        // The caller owns the transaction; the checkpoint advance joins it so
        // it commits or rolls back with the read-model write. A non-Npgsql
        // transaction here is a wiring bug, and the cast surfaces it loudly.
        var npgsqlTransaction = (NpgsqlTransaction)transaction;
        await using var cmd = npgsqlTransaction.Connection!.CreateCommand();
        cmd.Transaction = npgsqlTransaction;
        cmd.CommandText =
            "INSERT INTO read_models.projection_checkpoints AS pc " +
            "(projection_name, position) " +
            "VALUES (@projection_name, @position) " +
            "ON CONFLICT (projection_name) DO UPDATE " +
            "SET position = GREATEST(pc.position, EXCLUDED.position)";
        cmd.Parameters.AddWithValue("projection_name", NpgsqlDbType.Text, projectionName);
        cmd.Parameters.AddWithValue("position", NpgsqlDbType.Bigint, position);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
