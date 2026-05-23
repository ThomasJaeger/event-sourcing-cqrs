using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.ReadModels;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// One projection write against PostgreSQL: the OrderId-to-PaymentId mapping and
// the checkpoint advance run on a single NpgsqlTransaction, so CommitAsync makes
// both durable together and DisposeAsync without a commit rolls both back.
// Constructed by PostgresOrderIdToPaymentIdStore. Mirrors
// PostgresSkuToInventoryIdUnitOfWork.
internal sealed class PostgresOrderIdToPaymentIdUnitOfWork : IOrderIdToPaymentIdUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly ICheckpointStore _checkpointStore;

    public PostgresOrderIdToPaymentIdUnitOfWork(
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

    public async Task RecordAsync(Guid orderId, Guid paymentId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: an order has one authorized payment, so a
        // redelivered PaymentAuthorized for the same order leaves the first mapping
        // in place rather than erroring.
        cmd.CommandText =
            "INSERT INTO read_models.order_id_to_payment_id (order_id, payment_id) " +
            "VALUES (@order_id, @payment_id) " +
            "ON CONFLICT (order_id) DO NOTHING";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("payment_id", NpgsqlDbType.Uuid, paymentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CommitAsync(string projectionName, long position, CancellationToken ct)
    {
        // The checkpoint advance runs on this same transaction, so the mapping
        // write above and the checkpoint move commit as one unit.
        await _checkpointStore.AdvanceAsync(projectionName, position, _transaction, ct);
        await _transaction.CommitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        // If CommitAsync ran, disposing the transaction is a harmless no-op. If it
        // did not, the transaction rolls back, discarding the mapping write.
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
