using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// One projection write against PostgreSQL: the SKU mapping and the checkpoint
// advance run on a single NpgsqlTransaction, so CommitAsync makes both durable
// together and DisposeAsync without a commit rolls both back. Constructed by
// PostgresSkuToInventoryIdStore. Mirrors PostgresOrderListUnitOfWork.
internal sealed class PostgresSkuToInventoryIdUnitOfWork : ISkuToInventoryIdUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly ICheckpointStore _checkpointStore;

    public PostgresSkuToInventoryIdUnitOfWork(
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

    public async Task RecordAsync(string sku, Guid inventoryId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: a SKU maps to one InventoryId for its lifetime,
        // so a redelivered InventoryCreated for the same SKU leaves the first
        // mapping in place rather than erroring.
        cmd.CommandText =
            "INSERT INTO read_models.sku_to_inventory_id (sku, inventory_id) " +
            "VALUES (@sku, @inventory_id) " +
            "ON CONFLICT (sku) DO NOTHING";
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, sku);
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
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
        // If CommitAsync ran, disposing the transaction is a harmless no-op. If
        // it did not, the transaction rolls back, discarding the mapping write.
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
