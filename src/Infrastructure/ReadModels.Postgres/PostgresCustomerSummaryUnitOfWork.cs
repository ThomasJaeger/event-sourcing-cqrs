using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// One projection write against PostgreSQL: the summary mutation, the per-order
// lookup change, and the checkpoint advance run on a single NpgsqlTransaction,
// so CommitAsync makes them durable together and DisposeAsync without a commit
// rolls them back. Constructed by PostgresCustomerSummaryStore.
internal sealed class PostgresCustomerSummaryUnitOfWork : ICustomerSummaryUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly ICheckpointStore _checkpointStore;

    public PostgresCustomerSummaryUnitOfWork(
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

    public async Task ApplyPlacementAsync(
        Guid customerId, Money total, DateTime placedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // Upsert: the DO UPDATE branch reads cs (the existing row) and adds the
        // new placement. last_order_utc takes the GREATEST so a redelivery or an
        // out-of-order placement never regresses it; last_updated_utc overwrites
        // because it is system time, not event time.
        //
        // lifetime_value_currency is set only on insert and never touched on
        // update: v1 assumes one currency per customer (the Order aggregate seeds
        // Money.Zero(Currency.USD)), so amounts add under the existing currency.
        // A mixed-currency placement would add the amount under the wrong
        // currency. The defensive alternative reads the row's currency first and
        // throws on mismatch, which costs a read per placement for a v1-impossible
        // case, so v1 trusts the upstream single-currency invariant instead.
        cmd.CommandText =
            "INSERT INTO read_models.customer_summary AS cs " +
            "(customer_id, order_count, lifetime_value_amount, lifetime_value_currency, " +
            "last_order_utc, last_updated_utc) " +
            "VALUES (@customer_id, 1, @amount, @currency, @placed_utc, @last_updated_utc) " +
            "ON CONFLICT (customer_id) DO UPDATE " +
            "SET order_count = cs.order_count + 1, " +
            "lifetime_value_amount = cs.lifetime_value_amount + EXCLUDED.lifetime_value_amount, " +
            "last_order_utc = GREATEST(cs.last_order_utc, EXCLUDED.last_order_utc), " +
            "last_updated_utc = EXCLUDED.last_updated_utc";
        cmd.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
        cmd.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, total.Amount);
        cmd.Parameters.AddWithValue("currency", NpgsqlDbType.Text, total.Currency.Code);
        cmd.Parameters.AddWithValue("placed_utc", NpgsqlDbType.TimestampTz, placedUtc);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ApplyCancellationAsync(
        Guid customerId, Money total, DateTime lastUpdatedUtc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // Decrement and subtract; last_order_utc is left alone (ADR 0019's
        // accepted staleness). Same single-currency assumption as the placement
        // upsert: the subtraction trusts the cancelled order's currency matches.
        // Touches zero rows if no summary row exists, a harmless no-op (the
        // projection calls this only after a found lookup, so the row exists).
        cmd.CommandText =
            "UPDATE read_models.customer_summary " +
            "SET order_count = order_count - 1, " +
            "lifetime_value_amount = lifetime_value_amount - @amount, " +
            "last_updated_utc = @last_updated_utc " +
            "WHERE customer_id = @customer_id";
        cmd.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
        cmd.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, total.Amount);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, lastUpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertOrderAsync(CustomerSummaryOrderRow row, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // ON CONFLICT DO NOTHING: a redelivered OrderPlaced keeps the first
        // lookup row.
        cmd.CommandText =
            "INSERT INTO read_models.customer_summary_orders " +
            "(customer_id, order_id, total_amount, total_currency, placed_utc) " +
            "VALUES (@customer_id, @order_id, @total_amount, @total_currency, @placed_utc) " +
            "ON CONFLICT (customer_id, order_id) DO NOTHING";
        cmd.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, row.CustomerId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, row.OrderId);
        cmd.Parameters.AddWithValue("total_amount", NpgsqlDbType.Numeric, row.Total.Amount);
        cmd.Parameters.AddWithValue("total_currency", NpgsqlDbType.Text, row.Total.Currency.Code);
        cmd.Parameters.AddWithValue("placed_utc", NpgsqlDbType.TimestampTz, row.PlacedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<CustomerSummaryOrderRow?> GetOrderByOrderIdAsync(Guid orderId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // Keys on order_id alone (OrderCancelled carries no customer id), planned
        // against ix_customer_summary_orders_order_id.
        cmd.CommandText =
            "SELECT customer_id, order_id, total_amount, total_currency, placed_utc " +
            "FROM read_models.customer_summary_orders WHERE order_id = @order_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new CustomerSummaryOrderRow(
            CustomerId: reader.GetGuid(0),
            OrderId: reader.GetGuid(1),
            Total: new Money(reader.GetDecimal(2), new Currency(reader.GetString(3))),
            PlacedUtc: DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

    public async Task DeleteOrderAsync(Guid customerId, Guid orderId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        // Both ids in the WHERE: the primary key, so the delete is exact.
        cmd.CommandText =
            "DELETE FROM read_models.customer_summary_orders " +
            "WHERE customer_id = @customer_id AND order_id = @order_id";
        cmd.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CommitAsync(string projectionName, long position, CancellationToken ct)
    {
        // The checkpoint advance runs on this same transaction, so the summary
        // and lookup writes above and the checkpoint move commit as one unit.
        await _checkpointStore.AdvanceAsync(projectionName, position, _transaction, ct);
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
