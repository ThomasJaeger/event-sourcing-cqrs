using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using Npgsql;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// Chapter 9: SKU uniqueness belongs to the write side. Like the workflow lock, this
// companion uses PostgreSQL whichever engine stores events. A claim commits before the
// aggregate append and is never released on failure: the append may have committed even
// when its caller saw an error, and the same inventory id can safely retry the claim.
public sealed class PostgresInventorySkuRegistry(
    IReadModelConnectionFactory factory, IEventStore events) : IInventorySkuRegistry
{
    public async Task ClaimAsync(TenantId tenant, Guid inventoryId, string sku, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        await using var connection = await factory.OpenConnectionAsync(ct);
        await EnsureInitializedAsync(connection, ct);
        await ClaimRowAsync(connection, null, tenant, inventoryId, sku, ct);
    }

    private async Task EnsureInitializedAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        if (await IsInitializedAsync(connection, null, ct))
            return;

        await using var transaction = await connection.BeginTransactionAsync(ct);
        // The singleton row serializes the first backfill across hosts. Every creator
        // checks readiness before claiming; none can race an incomplete backfill.
        if (!await IsInitializedAsync(connection, transaction, ct))
        {
            try
            {
                await foreach (var envelope in events.ReadAllAsync(0, ct))
                {
                    if (envelope.Payload is InventoryCreated created)
                        await ClaimRowAsync(connection, transaction, envelope.Metadata.Tenant,
                            created.InventoryId, created.Sku, ct);
                }
            }
            catch (DomainException ex)
            {
                throw new InventorySkuRegistryInitializationException(ex);
            }

            await using var ready = connection.CreateCommand();
            ready.Transaction = transaction;
            ready.CommandText = "UPDATE write_side.inventory_sku_registry_state SET initialized = TRUE";
            await ready.ExecuteNonQueryAsync(ct);
        }
        // Commit initialization independently: a conflicting requested claim must not
        // undo the backfill or cause the next command to scan history again.
        await transaction.CommitAsync(ct);
    }

    private static async Task<bool> IsInitializedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT initialized FROM write_side.inventory_sku_registry_state " +
            "WHERE singleton = TRUE" + (transaction is null ? "" : " FOR UPDATE");
        return (bool)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Inventory SKU registry state has not been provisioned."));
    }

    private static async Task ClaimRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        TenantId tenant, Guid inventoryId, string sku, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("id", inventoryId);
        command.Parameters.AddWithValue("sku", sku);
        command.CommandText = "INSERT INTO write_side.inventory_sku_claims (tenant_id, inventory_id, sku) " +
            "VALUES (@tenant, @id, @sku) ON CONFLICT DO NOTHING";
        if (await command.ExecuteNonQueryAsync(ct) == 1)
            return;

        command.CommandText = "SELECT EXISTS (SELECT 1 FROM write_side.inventory_sku_claims " +
            "WHERE tenant_id = @tenant AND inventory_id = @id AND sku = @sku)";
        if (!(bool)(await command.ExecuteScalarAsync(ct))!)
            throw new DomainException(
                $"SKU '{sku}' or inventory id '{inventoryId}' is already assigned to another inventory mapping.");
    }
}
