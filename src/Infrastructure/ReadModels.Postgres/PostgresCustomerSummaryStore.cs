using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.SignalR;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// PostgreSQL implementation of ICustomerSummaryStore. BeginAsync opens a
// connection and transaction wrapped in a PostgresCustomerSummaryUnitOfWork.
// GetAsync and TruncateAsync open their own connections: the read path and the
// rebuild truncate need no transactional coordination with a handler's write.
public sealed class PostgresCustomerSummaryStore : ICustomerSummaryStore
{
    private readonly IReadModelConnectionFactory _factory;
    private readonly ICheckpointStore _checkpointStore;
    private readonly PostgresPgNotifyPublisher _publisher;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public PostgresCustomerSummaryStore(
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

    public async Task<ICustomerSummaryUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new PostgresCustomerSummaryUnitOfWork(connection, transaction, _checkpointStore, _publisher);
        }
        catch
        {
            // BeginTransactionAsync failed: the unit of work was never handed
            // back, so nothing else will dispose the connection.
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<CustomerSummaryRow?> GetAsync(Guid customerId, CancellationToken ct)
    {
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT customer_id, order_count, lifetime_value_amount, lifetime_value_currency, " +
            "last_order_utc, last_updated_utc " +
            "FROM read_models.customer_summary WHERE customer_id = @customer_id AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new CustomerSummaryRow(
            CustomerId: reader.GetGuid(0),
            OrderCount: reader.GetInt32(1),
            LifetimeValue: new Money(reader.GetDecimal(2), new Currency(reader.GetString(3))),
            LastOrderUtc: DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
            LastUpdatedUtc: DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc));
    }

    public async Task TruncateAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "TRUNCATE TABLE read_models.customer_summary, read_models.customer_summary_orders";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
