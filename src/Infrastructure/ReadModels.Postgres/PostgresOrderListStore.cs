using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Projections.OrderList;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// PostgreSQL implementation of IOrderListStore. BeginAsync opens a connection
// and transaction wrapped in a PostgresOrderListUnitOfWork. GetAsync and
// TruncateAsync open their own connections: the read path and the rebuild
// truncate need no transactional coordination with a handler's write.
public sealed class PostgresOrderListStore : IOrderListStore
{
    private readonly IReadModelConnectionFactory _factory;
    private readonly ICheckpointStore _checkpointStore;

    public PostgresOrderListStore(
        IReadModelConnectionFactory factory,
        ICheckpointStore checkpointStore)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        _factory = factory;
        _checkpointStore = checkpointStore;
    }

    public async Task<IOrderListUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new PostgresOrderListUnitOfWork(connection, transaction, _checkpointStore);
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
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT order_id, customer_id, status, total_amount, total_currency, " +
            "placed_utc, last_updated_utc " +
            "FROM read_models.order_list WHERE order_id = @order_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);

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
            Total: new Money(reader.GetDecimal(3), reader.GetString(4)),
            PlacedUtc: DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
            LastUpdatedUtc: DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc));
    }

    public async Task TruncateAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE read_models.order_list";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
