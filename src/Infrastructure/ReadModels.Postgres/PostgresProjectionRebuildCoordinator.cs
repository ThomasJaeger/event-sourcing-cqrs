using EventSourcingCqrs.Domain.Abstractions;
using Npgsql;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// The checkpoint row already serializes live handlers. Holding its transaction for
// the complete rebuild also coordinates independent worker and AdminConsole hosts.
// The replay uses RebuildModeCheckpointStore, so its separate write transactions do
// not try to acquire this lock again. No process-local mutex can provide this guarantee.
public sealed class PostgresProjectionRebuildCoordinator : IProjectionRebuildCoordinator
{
    private readonly IReadModelConnectionFactory _factory;
    private readonly ICheckpointStore _checkpointStore;

    public PostgresProjectionRebuildCoordinator(
        IReadModelConnectionFactory factory, ICheckpointStore checkpointStore)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        _factory = factory;
        _checkpointStore = checkpointStore;
    }

    public async Task<IProjectionRebuildLease> AcquireAsync(string projectionName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        var connection = await _factory.OpenConnectionAsync(ct);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(ct);
            var position = await _checkpointStore.GetPositionAsync(projectionName, transaction, ct);
            return new Lease(connection, transaction, position, projectionName);
        }
        catch
        {
            try
            {
                if (transaction is not null)
                {
                    await transaction.DisposeAsync();
                }
            }
            finally
            {
                await connection.DisposeAsync();
            }
            throw;
        }
    }

    private sealed class Lease(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long position,
        string projectionName) : IProjectionRebuildLease
    {
        public long Position => position;

        public async Task EnsureHeldAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(ct);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                throw new ProjectionRebuildLeaseLostException(projectionName, ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            // No checkpoint mutation is committed by a rebuild. Rollback releases the
            // lock on success, cancellation, or failure, including a first-use row insert.
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
