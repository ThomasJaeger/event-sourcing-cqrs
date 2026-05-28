using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.ReadModels;
using EventSourcingCqrs.Infrastructure.SignalR;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// PostgreSQL implementation of IOrderIdToPaymentIdStore. BeginAsync opens a
// connection and transaction wrapped in a PostgresOrderIdToPaymentIdUnitOfWork.
// GetPaymentIdAsync and TruncateAsync open their own connections: the read path
// and the rebuild truncate need no transactional coordination with a handler's
// write. Mirrors PostgresSkuToInventoryIdStore.
public sealed class PostgresOrderIdToPaymentIdStore : IOrderIdToPaymentIdStore
{
    private readonly IReadModelConnectionFactory _factory;
    private readonly ICheckpointStore _checkpointStore;
    private readonly PostgresPgNotifyPublisher _publisher;

    public PostgresOrderIdToPaymentIdStore(
        IReadModelConnectionFactory factory,
        ICheckpointStore checkpointStore,
        PostgresPgNotifyPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(publisher);
        _factory = factory;
        _checkpointStore = checkpointStore;
        _publisher = publisher;
    }

    public async Task<IOrderIdToPaymentIdUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new PostgresOrderIdToPaymentIdUnitOfWork(connection, transaction, _checkpointStore, _publisher);
        }
        catch
        {
            // BeginTransactionAsync failed: the unit of work was never handed
            // back, so nothing else will dispose the connection.
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<Guid?> GetPaymentIdAsync(Guid orderId, CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT payment_id FROM read_models.order_id_to_payment_id WHERE order_id = @order_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid id ? id : null;
    }

    public async Task TruncateAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE read_models.order_id_to_payment_id";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
