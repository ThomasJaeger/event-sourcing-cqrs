using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// One projection write against PostgreSQL: the row change and the checkpoint
// advance run on a single NpgsqlTransaction, so CommitAsync makes both durable
// together and DisposeAsync without a commit rolls both back. Constructed by
// PostgresOrderListStore.
internal sealed class PostgresOrderListUnitOfWork : IOrderListUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly ICheckpointStore _checkpointStore;

    public PostgresOrderListUnitOfWork(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICheckpointStore checkpointStore)
    {
        _connection = connection;
        _transaction = transaction;
        _checkpointStore = checkpointStore;
    }

    public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
        // Reads through the checkpoint store on this same transaction, so the
        // projection's idempotency check shares isolation with its row write.
        => _checkpointStore.GetPositionAsync(projectionName, _transaction, ct);

    public async Task InsertAsync(OrderListRow row, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING, not DO UPDATE: a redelivered OrderPlaced that
        // arrives after the order has shipped or been cancelled must not reset
        // the row to Placed. The first insert is the correct one.
        cmd.CommandText =
            "INSERT INTO read_models.order_list " +
            "(order_id, customer_id, status, total_amount, total_currency, " +
            "placed_utc, last_updated_utc) " +
            "VALUES (@order_id, @customer_id, @status, @total_amount, @total_currency, " +
            "@placed_utc, @last_updated_utc) " +
            "ON CONFLICT (order_id) DO NOTHING";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, row.OrderId);
        cmd.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, row.CustomerId);
        cmd.Parameters.AddWithValue("status", NpgsqlDbType.Text, row.Status.ToString());
        cmd.Parameters.AddWithValue("total_amount", NpgsqlDbType.Numeric, row.Total.Amount);
        cmd.Parameters.AddWithValue("total_currency", NpgsqlDbType.Text, row.Total.Currency.Code);
        cmd.Parameters.AddWithValue("placed_utc", NpgsqlDbType.TimestampTz, row.PlacedUtc);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, row.LastUpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateStatusAsync(
        Guid orderId, OrderStatus status, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // No row for orderId means the order was cancelled while still a draft
        // and was never placed; the update touches zero rows, which is correct.
        cmd.CommandText =
            "UPDATE read_models.order_list " +
            "SET status = @status, last_updated_utc = @last_updated_utc " +
            "WHERE order_id = @order_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("status", NpgsqlDbType.Text, status.ToString());
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertShipmentMappingAsync(
        Guid shipmentId, Guid orderId, DateTime scheduledUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: a redelivered ShipmentScheduled keeps the first
        // mapping row, mirroring the order_list insert's idempotency.
        cmd.CommandText =
            "INSERT INTO read_models.order_list_shipments " +
            "(shipment_id, order_id, scheduled_utc) " +
            "VALUES (@shipment_id, @order_id, @scheduled_utc) " +
            "ON CONFLICT (shipment_id) DO NOTHING";
        cmd.Parameters.AddWithValue("shipment_id", NpgsqlDbType.Uuid, shipmentId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("scheduled_utc", NpgsqlDbType.TimestampTz, scheduledUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Guid?> GetOrderIdByShipmentIdAsync(Guid shipmentId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText =
            "SELECT order_id FROM read_models.order_list_shipments " +
            "WHERE shipment_id = @shipment_id";
        cmd.Parameters.AddWithValue("shipment_id", NpgsqlDbType.Uuid, shipmentId);
        var result = await cmd.ExecuteScalarAsync(ct);
        // No mapping: the shipment was scheduled before this projection observed
        // it. The handler no-ops on null rather than throwing.
        return result is null or DBNull ? null : (Guid)result;
    }

    public async Task MarkReturnedAsync(
        Guid orderId, DateTime returnedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // No row for orderId means the order_list row was never created; the
        // update touches zero rows, which is harmless.
        cmd.CommandText =
            "UPDATE read_models.order_list " +
            "SET is_returned = true, returned_utc = @returned_utc, " +
            "last_updated_utc = @last_updated_utc " +
            "WHERE order_id = @order_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("returned_utc", NpgsqlDbType.TimestampTz, returnedUtc);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CommitAsync(string projectionName, long position, CancellationToken ct)
    {
        // The checkpoint advance runs on this same transaction, so the row
        // write above and the checkpoint move commit as one unit.
        await _checkpointStore.AdvanceAsync(projectionName, position, _transaction, ct);
        await _transaction.CommitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        // If CommitAsync ran, disposing the transaction is a harmless no-op. If
        // it did not, the transaction rolls back, discarding the row write.
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
