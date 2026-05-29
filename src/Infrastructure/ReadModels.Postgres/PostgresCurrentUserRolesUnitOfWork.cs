using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.ReadModels;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// One projection write against PostgreSQL: the role-row change and the checkpoint advance run on a
// single NpgsqlTransaction, so CommitAsync makes them durable together and DisposeAsync without a
// commit rolls them back. Constructed by PostgresCurrentUserRolesStore. Mirrors
// PostgresCustomerSummaryUnitOfWork, without the notification publish: the current-roles read model
// has no live-dashboard subscriber (born-at-consumer).
internal sealed class PostgresCurrentUserRolesUnitOfWork : ICurrentUserRolesUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly ICheckpointStore _checkpointStore;

    public PostgresCurrentUserRolesUnitOfWork(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICheckpointStore checkpointStore)
    {
        _connection = connection;
        _transaction = transaction;
        _checkpointStore = checkpointStore;
    }

    public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
        => _checkpointStore.GetPositionAsync(projectionName, _transaction, ct);

    public async Task AssignAsync(Guid userId, Role role, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: a redelivered RoleAssigned keeps the existing row.
        cmd.CommandText =
            "INSERT INTO read_models.current_user_roles (user_id, role) " +
            "VALUES (@user_id, @role) " +
            "ON CONFLICT (user_id, role) DO NOTHING";
        cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Uuid, userId);
        cmd.Parameters.AddWithValue("role", NpgsqlDbType.Text, role.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RevokeAsync(Guid userId, Role role, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // Touches zero rows if the assignment is absent, a harmless no-op.
        cmd.CommandText =
            "DELETE FROM read_models.current_user_roles " +
            "WHERE user_id = @user_id AND role = @role";
        cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Uuid, userId);
        cmd.Parameters.AddWithValue("role", NpgsqlDbType.Text, role.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CommitAsync(string projectionName, long position, CancellationToken ct)
    {
        // The checkpoint advance runs on this same transaction, so the role-row write above and the
        // checkpoint move commit as one unit.
        await _checkpointStore.AdvanceAsync(projectionName, position, _transaction, ct);
        await _transaction.CommitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        // If CommitAsync ran, disposing the transaction is a harmless no-op. If it did not, the
        // transaction rolls back, discarding the write.
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
