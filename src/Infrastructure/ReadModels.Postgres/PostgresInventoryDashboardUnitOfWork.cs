using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Infrastructure.SignalR;
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
    private readonly PostgresPgNotifyPublisher _publisher;
    private readonly ICurrentTenantAccessor _tenantAccessor;
    private NotificationEnvelope? _staged;

    public PostgresInventoryDashboardUnitOfWork(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICheckpointStore checkpointStore,
        PostgresPgNotifyPublisher publisher,
        ICurrentTenantAccessor tenantAccessor)
    {
        _connection = connection;
        _transaction = transaction;
        _checkpointStore = checkpointStore;
        _publisher = publisher;
        _tenantAccessor = tenantAccessor;
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
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "INSERT INTO read_models.inventory_dashboard (inventory_id, sku, last_updated_utc, tenant_id) " +
            "VALUES (@inventory_id, @sku, @last_updated_utc, @tenant_id) " +
            "ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, sku);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> AdjustOnHandAsync(
        Guid inventoryId, int quantityDelta, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // RETURNING sku hands back the mutated row's routing key in the same
        // statement, no write-path read. A zero-row UPDATE (no dashboard row for
        // this InventoryId, the ADR 0020 orphan case) returns null, and the
        // handler stages nothing.
        cmd.CommandText =
            "UPDATE read_models.inventory_dashboard " +
            "SET on_hand_quantity = on_hand_quantity + @delta, last_updated_utc = @last_updated_utc " +
            "WHERE inventory_id = @inventory_id " +
            "RETURNING sku";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("delta", NpgsqlDbType.Integer, quantityDelta);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<string?> AdjustReservedAsync(
        Guid inventoryId, int reservedDelta, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // RETURNING sku: see AdjustOnHandAsync. Null when no row matched.
        cmd.CommandText =
            "UPDATE read_models.inventory_dashboard " +
            "SET reserved_quantity = reserved_quantity + @delta, last_updated_utc = @last_updated_utc " +
            "WHERE inventory_id = @inventory_id " +
            "RETURNING sku";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("delta", NpgsqlDbType.Integer, reservedDelta);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task InsertReservationAsync(InventoryReservationRow row, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: a redelivered InventoryReserved keeps the first
        // lookup row.
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "INSERT INTO read_models.inventory_reservations " +
            "(inventory_id, order_id, line_id, quantity, reserved_utc, tenant_id) " +
            "VALUES (@inventory_id, @order_id, @line_id, @quantity, @reserved_utc, @tenant_id) " +
            "ON CONFLICT (inventory_id, order_id, line_id) DO NOTHING";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, row.InventoryId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, row.OrderId);
        cmd.Parameters.AddWithValue("line_id", NpgsqlDbType.Uuid, row.LineId);
        cmd.Parameters.AddWithValue("quantity", NpgsqlDbType.Integer, row.Quantity);
        cmd.Parameters.AddWithValue("reserved_utc", NpgsqlDbType.TimestampTz, row.ReservedUtc);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<InventoryReservationRow?> GetReservationAsync(
        Guid inventoryId, Guid orderId, Guid lineId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // Keys on the full (inventory_id, order_id, line_id) PK, all three carried
        // by InventoryReleased.
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "SELECT inventory_id, order_id, line_id, quantity, reserved_utc " +
            "FROM read_models.inventory_reservations " +
            "WHERE inventory_id = @inventory_id AND order_id = @order_id AND line_id = @line_id " +
            "AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("line_id", NpgsqlDbType.Uuid, lineId);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);

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
        // The checkpoint advance runs on this same transaction, so the dashboard
        // and lookup writes above and the checkpoint move commit as one unit.
        await _checkpointStore.AdvanceAsync(projectionName, position, _transaction, ct);
        // A staged notification rides the same transaction: pg_notify delivers it
        // to LISTEN subscribers at COMMIT and suppresses it on rollback. Issued
        // before the commit so an oversized payload faults here rather than after
        // the row write is already durable.
        if (_staged is not null)
        {
            await _publisher.PublishOnTransactionAsync(_staged, _transaction, ct);
        }
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
