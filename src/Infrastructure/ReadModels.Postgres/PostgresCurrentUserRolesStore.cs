using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.ReadModels;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// PostgreSQL implementation of ICurrentUserRolesStore. BeginAsync opens a connection and
// transaction wrapped in a PostgresCurrentUserRolesUnitOfWork. GetRolesForUserAsync opens its own
// connection: the read path needs no transactional coordination with a handler's write. Mirrors
// PostgresCustomerSummaryStore.
internal sealed class PostgresCurrentUserRolesStore : ICurrentUserRolesStore
{
    private readonly IReadModelConnectionFactory _factory;
    private readonly ICheckpointStore _checkpointStore;

    public PostgresCurrentUserRolesStore(
        IReadModelConnectionFactory factory,
        ICheckpointStore checkpointStore)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        _factory = factory;
        _checkpointStore = checkpointStore;
    }

    public async Task<ICurrentUserRolesUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new PostgresCurrentUserRolesUnitOfWork(connection, transaction, _checkpointStore);
        }
        catch
        {
            // BeginTransactionAsync failed: the unit of work was never handed back, so nothing
            // else will dispose the connection.
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyCollection<Role>> GetRolesForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        // Keyed on user_id, the leading column of the (user_id, role) primary key.
        cmd.CommandText =
            "SELECT role FROM read_models.current_user_roles WHERE user_id = @user_id";
        cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Uuid, userId);

        var roles = new List<Role>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            roles.Add(Enum.Parse<Role>(reader.GetString(0)));
        }
        return roles;
    }

    public async Task TruncateAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE read_models.current_user_roles";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
