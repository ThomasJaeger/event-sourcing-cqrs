using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.ReadModels;
using EventSourcingCqrs.Infrastructure.SignalR;
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
    private readonly PostgresPgNotifyPublisher _publisher;
    private NotificationEnvelope? _staged;

    public PostgresOrderIdToPaymentIdUnitOfWork(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICheckpointStore checkpointStore,
        PostgresPgNotifyPublisher publisher)
    {
        _connection = connection;
        _transaction = transaction;
        _checkpointStore = checkpointStore;
        _publisher = publisher;
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

    // No v1 consumer subscribes to this lookup, so the projection never stages a
    // notification; the member keeps the unit-of-work contract uniform across the
    // six read models and is ready when a consumer arrives.
    public void PublishOnCommit(NotificationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (_staged is not null)
        {
            throw new InvalidOperationException(
                "A unit of work stages at most one notification: one projection " +
                "handler processes one event and makes one logical change per commit.");
        }
        _staged = envelope;
    }

    public async Task CommitAsync(string projectionName, long position, CancellationToken ct)
    {
        // The checkpoint advance runs on this same transaction, so the mapping
        // write above and the checkpoint move commit as one unit.
        await _checkpointStore.AdvanceAsync(projectionName, position, _transaction, ct);
        if (_staged is not null)
        {
            await _publisher.PublishOnTransactionAsync(_staged, _transaction, ct);
        }
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
