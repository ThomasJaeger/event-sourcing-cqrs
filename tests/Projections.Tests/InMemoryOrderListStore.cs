using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;

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
    private readonly Dictionary<Guid, Guid> _shipmentToOrder = [];

    // Exposed so tests can assert the checkpoint advanced with the write.
    public Dictionary<string, long> Checkpoints { get; } = [];

    // Records each committed unit of work's staged notification, flushed on
    // CommitAsync so an uncommitted unit stages nothing the tests can observe.
    public List<NotificationEnvelope> StagedNotifications { get; } = [];

    public int InsertCount { get; private set; }

    public int UpdateCount { get; private set; }

    public Task<IOrderListUnitOfWork> BeginAsync(CancellationToken ct)
        => Task.FromResult<IOrderListUnitOfWork>(new UnitOfWork(this));

    public Task<OrderListRow?> GetAsync(Guid orderId, CancellationToken ct)
        => Task.FromResult(_rows.GetValueOrDefault(orderId));

    public Task<IReadOnlyList<OrderListRow>> GetPageAsync(int offset, int limit, CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit, 0, 200);
        var safeOffset = Math.Max(0, offset);
        IReadOnlyList<OrderListRow> page = _rows.Values
            .OrderByDescending(r => r.PlacedUtc)
            .ThenByDescending(r => r.OrderId)
            .Skip(safeOffset)
            .Take(clampedLimit)
            .ToList();
        return Task.FromResult(page);
    }

    public Task TruncateAsync(CancellationToken ct)
    {
        _rows.Clear();
        _shipmentToOrder.Clear();
        return Task.CompletedTask;
    }

    private sealed class UnitOfWork(InMemoryOrderListStore store) : IOrderListUnitOfWork
    {
        private NotificationEnvelope? _staged;

        public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(store.Checkpoints.GetValueOrDefault(projectionName));

        public Task InsertAsync(OrderListRow row, CancellationToken ct)
        {
            store.InsertCount++;
            // ON CONFLICT DO NOTHING: a redelivered insert leaves the first row.
            store._rows.TryAdd(row.OrderId, row);
            return Task.CompletedTask;
        }

        public Task<Guid?> UpdateStatusAsync(
            Guid orderId, OrderStatus status, DateTime lastUpdatedUtc, CancellationToken ct)
        {
            store.UpdateCount++;
            // Absent order_id: no-op and null, matching the SQL UPDATE touching zero rows.
            if (store._rows.TryGetValue(orderId, out var existing))
            {
                store._rows[orderId] = existing with
                {
                    Status = status,
                    LastUpdatedUtc = lastUpdatedUtc,
                };
                return Task.FromResult<Guid?>(existing.CustomerId);
            }
            return Task.FromResult<Guid?>(null);
        }

        public Task InsertShipmentMappingAsync(
            Guid shipmentId, Guid orderId, DateTime scheduledUtc, CancellationToken ct)
        {
            // ON CONFLICT DO NOTHING: a redelivered mapping keeps the first row.
            store._shipmentToOrder.TryAdd(shipmentId, orderId);
            return Task.CompletedTask;
        }

        public Task<Guid?> GetOrderIdByShipmentIdAsync(Guid shipmentId, CancellationToken ct)
            => Task.FromResult(
                store._shipmentToOrder.TryGetValue(shipmentId, out var orderId)
                    ? orderId
                    : (Guid?)null);

        public Task<Guid?> MarkReturnedAsync(
            Guid orderId, DateTime returnedUtc, DateTime lastUpdatedUtc, CancellationToken ct)
        {
            // Absent order_id: no-op and null, matching the SQL UPDATE touching zero rows.
            if (store._rows.TryGetValue(orderId, out var existing))
            {
                store._rows[orderId] = existing with
                {
                    IsReturned = true,
                    ReturnedUtc = returnedUtc,
                    LastUpdatedUtc = lastUpdatedUtc,
                };
                return Task.FromResult<Guid?>(existing.CustomerId);
            }
            return Task.FromResult<Guid?>(null);
        }

        public void PublishOnCommit(NotificationEnvelope envelope)
        {
            if (_staged is not null)
            {
                throw new InvalidOperationException(
                    "A unit of work stages at most one notification: one projection " +
                    "handler processes one event and makes one logical change per commit.");
            }
            _staged = envelope;
        }

        public Task CommitAsync(string projectionName, long position, CancellationToken ct)
        {
            // GREATEST, mirroring PostgresCheckpointStore's UPSERT.
            store.Checkpoints[projectionName] = Math.Max(
                store.Checkpoints.GetValueOrDefault(projectionName), position);
            // Flush the staged notification on commit, mirroring the Postgres unit
            // of work issuing pg_notify inside CommitAsync.
            if (_staged is not null)
            {
                store.StagedNotifications.Add(_staged);
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
