using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Infrastructure.SignalR;
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
    private readonly PostgresPgNotifyPublisher _publisher;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    // The accessor is held to thread into the unit of work, where RecordAsync tags the
    // write's tenant; GetInventoryIdAsync takes the tenant as a parameter (the process
    // manager reads it off the untenanted dispatch loop, so the accessor is not set there).
    public PostgresSkuToInventoryIdStore(
        IReadModelConnectionFactory factory,
        ICheckpointStore checkpointStore,
        PostgresPgNotifyPublisher publisher,
        ICurrentTenantAccessor tenantAccessor)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(tenantAccessor);
        _factory = factory;
        _checkpointStore = checkpointStore;
        _publisher = publisher;
        _tenantAccessor = tenantAccessor;
    }

    public async Task<ISkuToInventoryIdUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new PostgresSkuToInventoryIdUnitOfWork(
                connection, transaction, _checkpointStore, _publisher, _tenantAccessor);
        }
        catch
        {
            // BeginTransactionAsync failed: the unit of work was never handed
            // back, so nothing else will dispose the connection.
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<Guid?> GetInventoryIdAsync(string sku, TenantId tenant, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentNullException.ThrowIfNull(tenant);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT inventory_id FROM read_models.sku_to_inventory_id " +
            "WHERE sku = @sku AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, sku);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant.Value);
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
