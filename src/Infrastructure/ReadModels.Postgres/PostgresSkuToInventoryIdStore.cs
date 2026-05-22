using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// PostgreSQL implementation of ISkuToInventoryIdStore. BeginAsync opens a
// connection and transaction wrapped in a PostgresSkuToInventoryIdUnitOfWork.
// GetInventoryIdAsync and TruncateAsync open their own connections: the read
// path and the rebuild truncate need no transactional coordination with a
// handler's write. Mirrors PostgresOrderListStore.
public sealed class PostgresSkuToInventoryIdStore : ISkuToInventoryIdStore
{
    private readonly IReadModelConnectionFactory _factory;
    private readonly ICheckpointStore _checkpointStore;

    public PostgresSkuToInventoryIdStore(
        IReadModelConnectionFactory factory,
        ICheckpointStore checkpointStore)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        _factory = factory;
        _checkpointStore = checkpointStore;
    }

    public async Task<ISkuToInventoryIdUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new PostgresSkuToInventoryIdUnitOfWork(connection, transaction, _checkpointStore);
        }
        catch
        {
            // BeginTransactionAsync failed: the unit of work was never handed
            // back, so nothing else will dispose the connection.
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<Guid?> GetInventoryIdAsync(string sku, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT inventory_id FROM read_models.sku_to_inventory_id WHERE sku = @sku";
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, sku);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid id ? id : null;
    }

    public async Task TruncateAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE read_models.sku_to_inventory_id";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
