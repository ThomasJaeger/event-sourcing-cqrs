using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.SignalR;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// One projection write against PostgreSQL: the header, line, and timeline changes,
// any lookup change, and the checkpoint advance run on a single NpgsqlTransaction,
// so CommitAsync makes them durable together and DisposeAsync without a commit rolls
// them back. The five-table write is one atomic unit. Constructed by
// PostgresOrderDetailStore.
internal sealed class PostgresOrderDetailUnitOfWork : IOrderDetailUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly ICheckpointStore _checkpointStore;
    private readonly PostgresPgNotifyPublisher _publisher;
    private readonly ICurrentTenantAccessor _tenantAccessor;
    private NotificationEnvelope? _staged;

    public PostgresOrderDetailUnitOfWork(
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
        // Reads through the checkpoint store on this same transaction, so the
        // projection's idempotency check shares isolation with its write.
        => _checkpointStore.GetPositionAsync(projectionName, _transaction, ct);

    public async Task CreateHeaderAsync(
        Guid orderId, Guid customerId, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: a redelivered OrderDrafted keeps the first row.
        // The nullable columns take their NULL default until later events set them.
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "INSERT INTO read_models.order_detail (order_id, customer_id, status, last_updated_utc, tenant_id) " +
            "VALUES (@order_id, @customer_id, @status, @last_updated_utc, @tenant_id) " +
            "ON CONFLICT (tenant_id, order_id) DO NOTHING";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
        cmd.Parameters.AddWithValue("status", NpgsqlDbType.Text, OrderStatus.Draft.ToString());
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetShippingAddressAsync(
        Guid orderId, Address shippingAddress, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "UPDATE read_models.order_detail SET " +
            "shipping_address_street = @street, shipping_address_city = @city, " +
            "shipping_address_postal_code = @postal_code, shipping_address_country = @country, " +
            "last_updated_utc = @last_updated_utc WHERE order_id = @order_id AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);
        cmd.Parameters.AddWithValue("street", NpgsqlDbType.Text, shippingAddress.Street);
        cmd.Parameters.AddWithValue("city", NpgsqlDbType.Text, shippingAddress.City);
        cmd.Parameters.AddWithValue("postal_code", NpgsqlDbType.Text, shippingAddress.PostalCode);
        cmd.Parameters.AddWithValue("country", NpgsqlDbType.Text, shippingAddress.Country);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task ApplyPlacedAsync(
        Guid orderId, Money total, DateTime placedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        // OrderPlaced is the one transition that also writes the total.
        return ExecuteAsync(
            "UPDATE read_models.order_detail SET status = @status, placed_utc = @stamp, " +
            "total_amount = @total_amount, total_currency = @total_currency, " +
            "last_updated_utc = @last_updated_utc WHERE order_id = @order_id AND tenant_id = @tenant",
            orderId, OrderStatus.Placed, placedUtc, lastUpdatedUtc, ct,
            cmd =>
            {
                cmd.Parameters.AddWithValue("total_amount", NpgsqlDbType.Numeric, total.Amount);
                cmd.Parameters.AddWithValue("total_currency", NpgsqlDbType.Text, total.Currency.Code);
            });
    }

    public Task ApplyShippedAsync(
        Guid orderId, DateTime shippedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
        => ExecuteAsync(
            "UPDATE read_models.order_detail SET status = @status, shipped_utc = @stamp, " +
            "last_updated_utc = @last_updated_utc WHERE order_id = @order_id AND tenant_id = @tenant",
            orderId, OrderStatus.Shipped, shippedUtc, lastUpdatedUtc, ct);

    public Task ApplyCancelledAsync(
        Guid orderId, DateTime cancelledUtc, DateTime lastUpdatedUtc, CancellationToken ct)
        => ExecuteAsync(
            "UPDATE read_models.order_detail SET status = @status, cancelled_utc = @stamp, " +
            "last_updated_utc = @last_updated_utc WHERE order_id = @order_id AND tenant_id = @tenant",
            orderId, OrderStatus.Cancelled, cancelledUtc, lastUpdatedUtc, ct);

    public Task ApplyCompletedAsync(
        Guid orderId, DateTime completedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
        => ExecuteAsync(
            "UPDATE read_models.order_detail SET status = @status, completed_utc = @stamp, " +
            "last_updated_utc = @last_updated_utc WHERE order_id = @order_id AND tenant_id = @tenant",
            orderId, OrderStatus.Completed, completedUtc, lastUpdatedUtc, ct);

    // Shared body for the status transitions that write status, one *_utc column,
    // and last_updated_utc. Each owns a distinct *_utc column, so the SQL differs by
    // one column name; the binding is identical, the tenant predicate included, so
    // every caller's statement names @tenant. No row for orderId under this tenant
    // means the header was never created; the update touches zero rows, which is
    // correct.
    private async Task ExecuteAsync(
        string sql, Guid orderId, OrderStatus status, DateTime stamp, DateTime lastUpdatedUtc,
        CancellationToken ct, Action<NpgsqlCommand>? extraParameters = null)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue(
            "tenant", NpgsqlDbType.Uuid, ReadModelTenant.ResolveOrThrow(_tenantAccessor));
        cmd.Parameters.AddWithValue("status", NpgsqlDbType.Text, status.ToString());
        cmd.Parameters.AddWithValue("stamp", NpgsqlDbType.TimestampTz, stamp);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        extraParameters?.Invoke(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkReturnedAsync(
        Guid orderId, DateTime returnedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // Sets returned_utc only; the Sales status stays as it was (D5). No row for
        // orderId under this tenant means the header was never created; the update
        // touches zero rows.
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "UPDATE read_models.order_detail SET returned_utc = @returned_utc, " +
            "last_updated_utc = @last_updated_utc WHERE order_id = @order_id AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);
        cmd.Parameters.AddWithValue("returned_utc", NpgsqlDbType.TimestampTz, returnedUtc);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertLineAsync(OrderDetailLineRow row, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // Plain INSERT, no ON CONFLICT: the Order aggregate forbids adding a
        // currently-live LineId, and OrderLineRemoved frees the key, so a re-add
        // lands cleanly without ever conflicting on the (order_id, line_id) PK.
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "INSERT INTO read_models.order_detail_lines " +
            "(order_id, line_id, sku, quantity, unit_price_amount, unit_price_currency, tenant_id) " +
            "VALUES (@order_id, @line_id, @sku, @quantity, @unit_price_amount, @unit_price_currency, @tenant_id)";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, row.OrderId);
        cmd.Parameters.AddWithValue("line_id", NpgsqlDbType.Uuid, row.LineId);
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, row.Sku);
        cmd.Parameters.AddWithValue("quantity", NpgsqlDbType.Integer, row.Quantity);
        cmd.Parameters.AddWithValue("unit_price_amount", NpgsqlDbType.Numeric, row.UnitPrice.Amount);
        cmd.Parameters.AddWithValue("unit_price_currency", NpgsqlDbType.Text, row.UnitPrice.Currency.Code);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteLineAsync(Guid orderId, Guid lineId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "DELETE FROM read_models.order_detail_lines " +
            "WHERE order_id = @order_id AND line_id = @line_id AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("line_id", NpgsqlDbType.Uuid, lineId);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AppendTimelineAsync(OrderDetailTimelineRow row, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: a redelivery at the same (order_id, global_position)
        // keeps the first observation, the canonical record. The projection's
        // skip-guard is the primary defence; this is defence in depth.
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "INSERT INTO read_models.order_detail_timeline " +
            "(order_id, global_position, event_type, occurred_utc, payload, tenant_id) " +
            "VALUES (@order_id, @global_position, @event_type, @occurred_utc, @payload, @tenant_id) " +
            "ON CONFLICT (order_id, global_position) DO NOTHING";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, row.OrderId);
        cmd.Parameters.AddWithValue("global_position", NpgsqlDbType.Bigint, row.GlobalPosition);
        cmd.Parameters.AddWithValue("event_type", NpgsqlDbType.Text, row.EventType);
        cmd.Parameters.AddWithValue("occurred_utc", NpgsqlDbType.TimestampTz, row.OccurredUtc);
        // The JSON string binds to the jsonb column; Npgsql converts at the driver,
        // the same convention as PostgresEventStore and PostgresDelayQueue.
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, row.Payload);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertShipmentMappingAsync(OrderDetailShipmentRow row, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: a redelivered ShipmentScheduled keeps the first
        // mapping row. The mapping persists; nothing deletes it.
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "INSERT INTO read_models.order_detail_shipments (shipment_id, order_id, scheduled_utc, tenant_id) " +
            "VALUES (@shipment_id, @order_id, @scheduled_utc, @tenant_id) " +
            "ON CONFLICT (tenant_id, shipment_id) DO NOTHING";
        cmd.Parameters.AddWithValue("shipment_id", NpgsqlDbType.Uuid, row.ShipmentId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, row.OrderId);
        cmd.Parameters.AddWithValue("scheduled_utc", NpgsqlDbType.TimestampTz, row.ScheduledUtc);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Guid?> GetOrderIdByShipmentIdAsync(Guid shipmentId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "SELECT order_id FROM read_models.order_detail_shipments " +
            "WHERE shipment_id = @shipment_id AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("shipment_id", NpgsqlDbType.Uuid, shipmentId);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);
        var result = await cmd.ExecuteScalarAsync(ct);
        // No mapping: the shipment was scheduled before this projection observed it.
        // The handler no-ops on null rather than throwing. See ADR 0020.
        return result is null or DBNull ? null : (Guid)result;
    }

    public async Task InsertPaymentMappingAsync(OrderDetailPaymentRow row, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: a redelivered PaymentAuthorized keeps the first
        // mapping row. The mapping persists; nothing deletes it.
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "INSERT INTO read_models.order_detail_payments (payment_id, order_id, authorized_utc, tenant_id) " +
            "VALUES (@payment_id, @order_id, @authorized_utc, @tenant_id) " +
            "ON CONFLICT (tenant_id, payment_id) DO NOTHING";
        cmd.Parameters.AddWithValue("payment_id", NpgsqlDbType.Uuid, row.PaymentId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, row.OrderId);
        cmd.Parameters.AddWithValue("authorized_utc", NpgsqlDbType.TimestampTz, row.AuthorizedUtc);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Guid?> GetOrderIdByPaymentIdAsync(Guid paymentId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        cmd.CommandText =
            "SELECT order_id FROM read_models.order_detail_payments " +
            "WHERE payment_id = @payment_id AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("payment_id", NpgsqlDbType.Uuid, paymentId);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);
        var result = await cmd.ExecuteScalarAsync(ct);
        // No mapping: the payment was authorized before this projection observed it.
        // The handler no-ops on null rather than throwing. See ADR 0020.
        return result is null or DBNull ? null : (Guid)result;
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
        // The checkpoint advance runs on this same transaction, so the five-table
        // write above and the checkpoint move commit as one unit.
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
        // If CommitAsync ran, disposing the transaction is a harmless no-op. If it
        // did not, the transaction rolls back, discarding every write above.
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
