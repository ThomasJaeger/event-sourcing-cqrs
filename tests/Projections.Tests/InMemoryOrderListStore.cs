using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Projections.OrderList;

namespace EventSourcingCqrs.Projections.Tests;

// In-memory IOrderListStore for OrderListProjectionTests. It records what the
// handlers do; it does not simulate transaction isolation. Writes apply
// immediately to the backing dictionary and CommitAsync just records the
// checkpoint, because the projection always commits and the tests assert on
// the committed result. Rollback behaviour is exercised against the real
// database in PostgresOrderListStoreTests.
//
// InsertCount and UpdateCount let the redelivery-skip tests assert that the
// projection's early-return path bailed out before touching the unit of work,
// not just that the row ended up unchanged (which would also be true under
// SQL-level ON CONFLICT DO NOTHING).
internal sealed class InMemoryOrderListStore : IOrderListStore
{
    private readonly Dictionary<Guid, OrderListRow> _rows = [];

    // Exposed so tests can assert the checkpoint advanced with the write.
    public Dictionary<string, long> Checkpoints { get; } = [];

    public int InsertCount { get; private set; }

    public int UpdateCount { get; private set; }

    public Task<IOrderListUnitOfWork> BeginAsync(CancellationToken ct)
        => Task.FromResult<IOrderListUnitOfWork>(new UnitOfWork(this));

    public Task<OrderListRow?> GetAsync(Guid orderId, CancellationToken ct)
        => Task.FromResult(_rows.GetValueOrDefault(orderId));

    public Task TruncateAsync(CancellationToken ct)
    {
        _rows.Clear();
        return Task.CompletedTask;
    }

    private sealed class UnitOfWork(InMemoryOrderListStore store) : IOrderListUnitOfWork
    {
        public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(store.Checkpoints.GetValueOrDefault(projectionName));

        public Task InsertAsync(OrderListRow row, CancellationToken ct)
        {
            store.InsertCount++;
            // ON CONFLICT DO NOTHING: a redelivered insert leaves the first row.
            store._rows.TryAdd(row.OrderId, row);
            return Task.CompletedTask;
        }

        public Task UpdateStatusAsync(
            Guid orderId, OrderStatus status, DateTime lastUpdatedUtc, CancellationToken ct)
        {
            store.UpdateCount++;
            // Absent order_id: no-op, matching the SQL UPDATE touching zero rows.
            if (store._rows.TryGetValue(orderId, out var existing))
            {
                store._rows[orderId] = existing with
                {
                    Status = status,
                    LastUpdatedUtc = lastUpdatedUtc,
                };
            }
            return Task.CompletedTask;
        }

        public Task CommitAsync(string projectionName, long position, CancellationToken ct)
        {
            // GREATEST, mirroring PostgresCheckpointStore's UPSERT.
            store.Checkpoints[projectionName] = Math.Max(
                store.Checkpoints.GetValueOrDefault(projectionName), position);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
