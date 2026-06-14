using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Infrastructure.SignalR;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// PostgreSQL implementation of IOrderThroughputStore, mirroring
// PostgresInventoryDashboardStore. RED #4 placeholder: the structural plumbing
// (connection/transaction, checkpoint, notification, commit) is wired, but the
// data-access methods do not yet persist or read, so the integration test fails on
// the missing buckets. The real upsert/prune/select land in the GREEN turn.
public sealed class PostgresOrderThroughputStore : IOrderThroughputStore
{
    private readonly IReadModelConnectionFactory _factory;
    private readonly ICheckpointStore _checkpointStore;
    private readonly PostgresPgNotifyPublisher _publisher;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public PostgresOrderThroughputStore(
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

    public async Task<IOrderThroughputUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new PostgresOrderThroughputUnitOfWork(
                connection, transaction, _checkpointStore, _publisher, _tenantAccessor);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // Every per-second bucket for the current tenant, ordered by second. The read
    // opens its own connection, the same shape as the inventory dashboard's GetAll.
    public async Task<IReadOnlyList<OrderThroughputRow>> GetBucketsAsync(CancellationToken ct)
    {
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT second_utc, count FROM read_models.order_throughput " +
            "WHERE tenant_id = @tenant ORDER BY second_utc";
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<OrderThroughputRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OrderThroughputRow(
                SecondUtc: DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                Count: reader.GetInt64(1)));
        }
        return rows;
    }

    // RED #4 placeholder: no-op. The GREEN turn truncates read_models.order_throughput.
    public Task TruncateAsync(CancellationToken ct) => Task.CompletedTask;
}

// PostgreSQL unit of work for the order-throughput read model. RED #4 placeholder:
// the checkpoint, notification, and commit plumbing mirrors
// PostgresInventoryDashboardUnitOfWork; the count and prune methods do not yet
// persist, so a committed write leaves the table empty.
internal sealed class PostgresOrderThroughputUnitOfWork : IOrderThroughputUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly ICheckpointStore _checkpointStore;
    private readonly PostgresPgNotifyPublisher _publisher;
    private readonly ICurrentTenantAccessor _tenantAccessor;
    private NotificationEnvelope? _staged;

    public PostgresOrderThroughputUnitOfWork(
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

    // Count one order event into its occurrence-second bucket for the current tenant.
    // The same UPSERT idiom as PostgresCheckpointStore.AdvanceAsync: a second already
    // present collides on the (tenant_id, second_utc) PK and increments its count.
    public async Task IncrementSecondAsync(DateTime secondUtc, CancellationToken ct)
    {
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText =
            "INSERT INTO read_models.order_throughput (tenant_id, second_utc, count) " +
            "VALUES (@tenant, @second, 1) " +
            "ON CONFLICT (tenant_id, second_utc) DO UPDATE " +
            "SET count = order_throughput.count + 1";
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);
        cmd.Parameters.AddWithValue("second", NpgsqlDbType.TimestampTz, secondUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Prune the current tenant's buckets strictly older than the cutoff. Strict < so
    // the bucket exactly at the cutoff is retained (the GREEN #2 boundary rule).
    public async Task PruneBeforeAsync(DateTime cutoffSecond, CancellationToken ct)
    {
        var tenant = ReadModelTenant.ResolveOrThrow(_tenantAccessor);
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText =
            "DELETE FROM read_models.order_throughput " +
            "WHERE tenant_id = @tenant AND second_utc < @cutoff";
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant);
        cmd.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoffSecond);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void PublishOnCommit(NotificationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        _staged = envelope;
    }

    public async Task CommitAsync(string projectionName, long position, CancellationToken ct)
    {
        await _checkpointStore.AdvanceAsync(projectionName, position, _transaction, ct);
        if (_staged is not null)
        {
            await _publisher.PublishOnTransactionAsync(_staged, _transaction, ct);
        }
        await _transaction.CommitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
