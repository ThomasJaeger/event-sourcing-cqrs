using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Infrastructure.SignalR;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// PostgreSQL implementation of IInventoryDashboardStore. BeginAsync opens a
// connection and transaction wrapped in a PostgresInventoryDashboardUnitOfWork.
// GetBySkuAsync, GetAllAsync, and TruncateAsync open their own connections: the
// read paths and the rebuild truncate need no transactional coordination with a
// handler's write.
public sealed class PostgresInventoryDashboardStore : IInventoryDashboardStore
{
    private readonly IReadModelConnectionFactory _factory;
    private readonly ICheckpointStore _checkpointStore;
    private readonly PostgresPgNotifyPublisher _publisher;

    public PostgresInventoryDashboardStore(
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

    public async Task<IInventoryDashboardUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new PostgresInventoryDashboardUnitOfWork(connection, transaction, _checkpointStore, _publisher);
        }
        catch
        {
            // BeginTransactionAsync failed: the unit of work was never handed
            // back, so nothing else will dispose the connection.
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<InventoryDashboardRow?> GetBySkuAsync(string sku, CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT inventory_id, sku, on_hand_quantity, reserved_quantity, last_updated_utc " +
            "FROM read_models.inventory_dashboard WHERE sku = @sku";
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, sku);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new InventoryDashboardRow(
            InventoryId: reader.GetGuid(0),
            Sku: reader.GetString(1),
            OnHandQuantity: reader.GetInt32(2),
            ReservedQuantity: reader.GetInt32(3),
            LastUpdatedUtc: DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

    public async Task<IReadOnlyList<InventoryDashboardRow>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        // Ordered by sku so the dashboard list is deterministic for its consumers.
        cmd.CommandText =
            "SELECT inventory_id, sku, on_hand_quantity, reserved_quantity, last_updated_utc " +
            "FROM read_models.inventory_dashboard ORDER BY sku";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<InventoryDashboardRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new InventoryDashboardRow(
                InventoryId: reader.GetGuid(0),
                Sku: reader.GetString(1),
                OnHandQuantity: reader.GetInt32(2),
                ReservedQuantity: reader.GetInt32(3),
                LastUpdatedUtc: DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));
        }
        return rows;
    }

    public async Task TruncateAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "TRUNCATE TABLE read_models.inventory_dashboard, read_models.inventory_reservations";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
