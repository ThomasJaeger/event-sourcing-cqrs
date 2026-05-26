using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// One projection write against PostgreSQL: the dashboard mutation, the
// per-reservation lookup change, and the checkpoint advance run on a single
// NpgsqlTransaction, so CommitAsync makes them durable together and DisposeAsync
// without a commit rolls them back. Constructed by PostgresInventoryDashboardStore.
internal sealed class PostgresInventoryDashboardUnitOfWork : IInventoryDashboardUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly ICheckpointStore _checkpointStore;

    public PostgresInventoryDashboardUnitOfWork(
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

    public async Task CreateDashboardAsync(
        Guid inventoryId, string sku, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // Untargeted ON CONFLICT DO NOTHING, covering both unique constraints on
        // the table: a redelivered InventoryCreated conflicts on the inventory_id
        // PK, and a duplicate-SKU create under a different InventoryId conflicts on
        // the sku UNIQUE constraint (migration 0013). Both no-op, so a duplicate SKU
        // never faults the projection, mirroring SkuToInventoryIdUnitOfWork.
        // RecordAsync. The first SKU owner keeps its row; a conflicting create
        // leaves an orphan aggregate with no dashboard row, the read-side stance of
        // ADR 0020. on_hand_quantity and reserved_quantity take their DDL defaults
        // of 0.
        cmd.CommandText =
            "INSERT INTO read_models.inventory_dashboard (inventory_id, sku, last_updated_utc) " +
            "VALUES (@inventory_id, @sku, @last_updated_utc) " +
            "ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, sku);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AdjustOnHandAsync(
        Guid inventoryId, int quantityDelta, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText =
            "UPDATE read_models.inventory_dashboard " +
            "SET on_hand_quantity = on_hand_quantity + @delta, last_updated_utc = @last_updated_utc " +
            "WHERE inventory_id = @inventory_id";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("delta", NpgsqlDbType.Integer, quantityDelta);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AdjustReservedAsync(
        Guid inventoryId, int reservedDelta, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText =
            "UPDATE read_models.inventory_dashboard " +
            "SET reserved_quantity = reserved_quantity + @delta, last_updated_utc = @last_updated_utc " +
            "WHERE inventory_id = @inventory_id";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("delta", NpgsqlDbType.Integer, reservedDelta);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertReservationAsync(InventoryReservationRow row, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: a redelivered InventoryReserved keeps the first
        // lookup row.
        cmd.CommandText =
            "INSERT INTO read_models.inventory_reservations " +
            "(inventory_id, order_id, line_id, quantity, reserved_utc) " +
            "VALUES (@inventory_id, @order_id, @line_id, @quantity, @reserved_utc) " +
            "ON CONFLICT (inventory_id, order_id, line_id) DO NOTHING";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, row.InventoryId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, row.OrderId);
        cmd.Parameters.AddWithValue("line_id", NpgsqlDbType.Uuid, row.LineId);
        cmd.Parameters.AddWithValue("quantity", NpgsqlDbType.Integer, row.Quantity);
        cmd.Parameters.AddWithValue("reserved_utc", NpgsqlDbType.TimestampTz, row.ReservedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<InventoryReservationRow?> GetReservationAsync(
        Guid inventoryId, Guid orderId, Guid lineId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // Keys on the full (inventory_id, order_id, line_id) PK, all three carried
        // by InventoryReleased.
        cmd.CommandText =
            "SELECT inventory_id, order_id, line_id, quantity, reserved_utc " +
            "FROM read_models.inventory_reservations " +
            "WHERE inventory_id = @inventory_id AND order_id = @order_id AND line_id = @line_id";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("line_id", NpgsqlDbType.Uuid, lineId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new InventoryReservationRow(
            InventoryId: reader.GetGuid(0),
            OrderId: reader.GetGuid(1),
            LineId: reader.GetGuid(2),
            Quantity: reader.GetInt32(3),
            ReservedUtc: DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

    public async Task DeleteReservationAsync(
        Guid inventoryId, Guid orderId, Guid lineId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText =
            "DELETE FROM read_models.inventory_reservations " +
            "WHERE inventory_id = @inventory_id AND order_id = @order_id AND line_id = @line_id";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("line_id", NpgsqlDbType.Uuid, lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CommitAsync(string projectionName, long position, CancellationToken ct)
    {
        // The checkpoint advance runs on this same transaction, so the dashboard
        // and lookup writes above and the checkpoint move commit as one unit.
        await _checkpointStore.AdvanceAsync(projectionName, position, _transaction, ct);
        await _transaction.CommitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        // If CommitAsync ran, disposing the transaction is a harmless no-op. If
        // it did not, the transaction rolls back, discarding the writes.
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
