using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.SignalR;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// PostgreSQL implementation of IOrderDetailStore. BeginAsync opens a connection
// and transaction wrapped in a PostgresOrderDetailUnitOfWork. GetHeaderAsync,
// GetLinesAsync, GetTimelineAsync, and TruncateAsync open their own connections:
// the read paths and the rebuild truncate need no transactional coordination with
// a handler's write.
public sealed class PostgresOrderDetailStore : IOrderDetailStore
{
    private readonly IReadModelConnectionFactory _factory;
    private readonly ICheckpointStore _checkpointStore;
    private readonly PostgresPgNotifyPublisher _publisher;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public PostgresOrderDetailStore(
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

    public async Task<IOrderDetailUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new PostgresOrderDetailUnitOfWork(connection, transaction, _checkpointStore, _publisher);
        }
        catch
        {
            // BeginTransactionAsync failed: the unit of work was never handed
            // back, so nothing else will dispose the connection.
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<OrderDetailRow?> GetHeaderAsync(Guid orderId, CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT order_id, customer_id, status, placed_utc, shipped_utc, cancelled_utc, " +
            "completed_utc, returned_utc, total_amount, total_currency, " +
            "shipping_address_street, shipping_address_city, shipping_address_postal_code, " +
            "shipping_address_country, last_updated_utc " +
            "FROM read_models.order_detail WHERE order_id = @order_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new OrderDetailRow(
            OrderId: reader.GetGuid(0),
            CustomerId: reader.GetGuid(1),
            // Case-sensitive: a lowercase status in the column is a data integrity
            // bug, not something to read gracefully.
            Status: Enum.Parse<OrderStatus>(reader.GetString(2)),
            PlacedUtc: ReadNullableUtc(reader, 3),
            ShippedUtc: ReadNullableUtc(reader, 4),
            CancelledUtc: ReadNullableUtc(reader, 5),
            CompletedUtc: ReadNullableUtc(reader, 6),
            ReturnedUtc: ReadNullableUtc(reader, 7),
            // total_amount and total_currency move together: both null until
            // OrderPlaced sets them.
            Total: reader.IsDBNull(8)
                ? null
                : new Money(reader.GetDecimal(8), new Currency(reader.GetString(9))),
            // The four address columns move together: all null until
            // ShippingAddressSet writes them.
            ShippingAddress: reader.IsDBNull(10)
                ? null
                : new Address(
                    reader.GetString(10), reader.GetString(11),
                    reader.GetString(12), reader.GetString(13)),
            LastUpdatedUtc: DateTime.SpecifyKind(reader.GetDateTime(14), DateTimeKind.Utc));
    }

    public async Task<IReadOnlyList<OrderDetailLineRow>> GetLinesAsync(Guid orderId, CancellationToken ct)
    {
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        // ORDER BY line_id for deterministic test reads; lines have no intrinsic
        // order from the events.
        cmd.CommandText =
            "SELECT order_id, line_id, sku, quantity, unit_price_amount, unit_price_currency " +
            "FROM read_models.order_detail_lines WHERE order_id = @order_id AND tenant_id = @tenant " +
            "ORDER BY line_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<OrderDetailLineRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OrderDetailLineRow(
                OrderId: reader.GetGuid(0),
                LineId: reader.GetGuid(1),
                Sku: reader.GetString(2),
                Quantity: reader.GetInt32(3),
                UnitPrice: new Money(reader.GetDecimal(4), new Currency(reader.GetString(5)))));
        }
        return rows;
    }

    public async Task<IReadOnlyList<OrderDetailTimelineRow>> GetTimelineAsync(
        Guid orderId, CancellationToken ct)
    {
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        // ORDER BY global_position: the order the events were observed.
        cmd.CommandText =
            "SELECT order_id, global_position, event_type, occurred_utc, payload " +
            "FROM read_models.order_detail_timeline WHERE order_id = @order_id AND tenant_id = @tenant " +
            "ORDER BY global_position";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<OrderDetailTimelineRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OrderDetailTimelineRow(
                OrderId: reader.GetGuid(0),
                GlobalPosition: reader.GetInt64(1),
                EventType: reader.GetString(2),
                OccurredUtc: DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                // jsonb reads back as canonicalised text (keys sorted, whitespace
                // normalised). Callers parse it; they do not byte-compare.
                Payload: reader.GetString(4)));
        }
        return rows;
    }

    public async Task TruncateAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "TRUNCATE TABLE read_models.order_detail, read_models.order_detail_lines, " +
            "read_models.order_detail_timeline, read_models.order_detail_shipments, " +
            "read_models.order_detail_payments";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DateTime? ReadNullableUtc(Npgsql.NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
}
