using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Infrastructure.SignalR;
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
    private readonly PostgresPgNotifyPublisher _publisher;
    private readonly ICurrentTenantAccessor _tenantAccessor;
    private NotificationEnvelope? _staged;

    public PostgresSkuToInventoryIdUnitOfWork(
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

    public async Task RecordAsync(string sku, Guid inventoryId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT (tenant_id, sku) DO NOTHING: within a tenant a SKU maps to one
        // InventoryId for its lifetime, so a redelivered InventoryCreated for the same
        // SKU leaves the first mapping; a different tenant's same SKU is a distinct key
        // and records its own row. The tenant comes from the current-tenant accessor the
        // dispatch boundary set, the same path the four public read models tag through.
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "INSERT INTO read_models.sku_to_inventory_id (sku, inventory_id, tenant_id) " +
            "VALUES (@sku, @inventory_id, @tenant_id) " +
            "ON CONFLICT (tenant_id, sku) DO NOTHING";
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, sku);
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
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
        // If CommitAsync ran, disposing the transaction is a harmless no-op. If
        // it did not, the transaction rolls back, discarding the mapping write.
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
