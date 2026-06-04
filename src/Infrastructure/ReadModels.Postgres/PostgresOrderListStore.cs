using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.SignalR;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// PostgreSQL implementation of IOrderListStore. BeginAsync opens a connection
// and transaction wrapped in a PostgresOrderListUnitOfWork. GetAsync and
// TruncateAsync open their own connections: the read path and the rebuild
// truncate need no transactional coordination with a handler's write.
public sealed class PostgresOrderListStore : IOrderListStore, ITenantResettable
{
    private readonly IReadModelConnectionFactory _factory;
    private readonly ICheckpointStore _checkpointStore;
    private readonly PostgresPgNotifyPublisher _publisher;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public PostgresOrderListStore(
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

    public async Task<IOrderListUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new PostgresOrderListUnitOfWork(
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

    public async Task<OrderListRow?> GetAsync(Guid orderId, CancellationToken ct)
    {
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT order_id, customer_id, status, total_amount, total_currency, " +
            "placed_utc, last_updated_utc, is_returned, returned_utc " +
            "FROM read_models.order_list WHERE order_id = @order_id AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new OrderListRow(
            OrderId: reader.GetGuid(0),
            CustomerId: reader.GetGuid(1),
            // Case-sensitive: a lowercase "placed" in the column is a data
            // integrity bug, not something to read gracefully.
            Status: Enum.Parse<OrderStatus>(reader.GetString(2)),
            Total: new Money(reader.GetDecimal(3), new Currency(reader.GetString(4))),
            PlacedUtc: DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
            LastUpdatedUtc: DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
            IsReturned: reader.GetBoolean(7),
            ReturnedUtc: reader.IsDBNull(8)
                ? null
                : DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc));
    }

    public async Task<IReadOnlyList<OrderListRow>> GetPageAsync(
        int offset, int limit, CancellationToken ct)
    {
        // Resolve the tenant before the clamp and the limit-zero early return, so a
        // page read with no tenant set fails closed on every path.
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);

        // Defense-in-depth clamp. 200 covers every reasonable page size for
        // a list view; the caller still gets a deterministic answer for a
        // pathological request rather than a slow scan of the whole table.
        var clampedLimit = Math.Clamp(limit, 0, 200);
        var safeOffset = Math.Max(0, offset);
        if (clampedLimit == 0)
        {
            return Array.Empty<OrderListRow>();
        }

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT order_id, customer_id, status, total_amount, total_currency, " +
            "placed_utc, last_updated_utc, is_returned, returned_utc " +
            "FROM read_models.order_list " +
            "WHERE tenant_id = @tenant " +
            "ORDER BY placed_utc DESC, order_id DESC " +
            "LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);
        cmd.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, clampedLimit);
        cmd.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, safeOffset);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<OrderListRow>(clampedLimit);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OrderListRow(
                OrderId: reader.GetGuid(0),
                CustomerId: reader.GetGuid(1),
                Status: Enum.Parse<OrderStatus>(reader.GetString(2)),
                Total: new Money(reader.GetDecimal(3), new Currency(reader.GetString(4))),
                PlacedUtc: DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                LastUpdatedUtc: DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                IsReturned: reader.GetBoolean(7),
                ReturnedUtc: reader.IsDBNull(8)
                    ? null
                    : DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)));
        }
        return rows;
    }

    public async Task TruncateAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "TRUNCATE TABLE read_models.order_list, read_models.order_list_shipments";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Tenant-scoped twin of TruncateAsync: deletes only the given tenant's rows so a
    // per-tenant rebuild starts that tenant from empty without touching other tenants or
    // the global checkpoint (ITenantResettable). Never a TRUNCATE.
    public async Task ResetTenantAsync(TenantId tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "DELETE FROM read_models.order_list WHERE tenant_id = @tenant; " +
            "DELETE FROM read_models.order_list_shipments WHERE tenant_id = @tenant";
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
