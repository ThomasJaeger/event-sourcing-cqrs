using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Projections.OrderList;

namespace EventSourcingCqrs.Projections.Tests;

// In-memory IOrderListStore for OrderListProjectionTests. It records what the
// handlers do; it does not simulate transaction isolation. Writes apply
// immediately to the backing dictionary and CommitAsync just records the
// checkpoint, because the projection always commits and the tests assert on
// the committed result. Rollback behaviour is exercised against the real
// database in PostgresOrderListStoreTests.
internal sealed class InMemoryOrderListStore : IOrderListStore
{
    private readonly Dictionary<Guid, OrderListRow> _rows = [];

    // Exposed so tests can assert the checkpoint advanced with the write.
    public Dictionary<string, long> Checkpoints { get; } = [];

    public Task<IOrderListUnitOfWork> BeginAsync(CancellationToken ct)
        => Task.FromResult<IOrderListUnitOfWork>(new UnitOfWork(_rows, Checkpoints));

    public Task<OrderListRow?> GetAsync(Guid orderId, CancellationToken ct)
        => Task.FromResult(_rows.GetValueOrDefault(orderId));

    public Task TruncateAsync(CancellationToken ct)
    {
        _rows.Clear();
        return Task.CompletedTask;
    }

    private sealed class UnitOfWork(
        Dictionary<Guid, OrderListRow> rows,
        Dictionary<string, long> checkpoints) : IOrderListUnitOfWork
    {
        public Task InsertAsync(OrderListRow row, CancellationToken ct)
        {
            // ON CONFLICT DO NOTHING: a redelivered insert leaves the first row.
            rows.TryAdd(row.OrderId, row);
            return Task.CompletedTask;
        }

        public Task UpdateStatusAsync(
            Guid orderId, OrderStatus status, DateTime lastUpdatedUtc, CancellationToken ct)
        {
            // Absent order_id: no-op, matching the SQL UPDATE touching zero rows.
            if (rows.TryGetValue(orderId, out var existing))
            {
                rows[orderId] = existing with { Status = status, LastUpdatedUtc = lastUpdatedUtc };
            }
            return Task.CompletedTask;
        }

        public Task CommitAsync(string projectionName, long position, CancellationToken ct)
        {
            // GREATEST, mirroring PostgresCheckpointStore's UPSERT.
            checkpoints[projectionName] = Math.Max(
                checkpoints.GetValueOrDefault(projectionName), position);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
