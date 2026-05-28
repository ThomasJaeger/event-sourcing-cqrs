using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;

namespace EventSourcingCqrs.Projections.Tests;

// In-memory IInventoryDashboardStore for the projection tests and the in-memory
// behaviour tests. Writes apply immediately and CommitAsync records the
// checkpoint, because the projection always commits and the tests assert on the
// committed result. Rollback is exercised against the real database in
// PostgresInventoryDashboardStoreTests.
internal sealed class InMemoryInventoryDashboardStore : IInventoryDashboardStore
{
    private readonly Dictionary<Guid, InventoryDashboardRow> _dashboard = [];
    private readonly Dictionary<(Guid InventoryId, Guid OrderId, Guid LineId), InventoryReservationRow> _reservations = [];

    // Exposed so tests can assert the checkpoint advanced with the write.
    public Dictionary<string, long> Checkpoints { get; } = [];

    // Records each committed unit of work's staged notification, flushed on
    // CommitAsync so an uncommitted unit stages nothing the tests can observe.
    public List<NotificationEnvelope> StagedNotifications { get; } = [];

    public Task<IInventoryDashboardUnitOfWork> BeginAsync(CancellationToken ct)
        => Task.FromResult<IInventoryDashboardUnitOfWork>(new UnitOfWork(this));

    public Task<InventoryDashboardRow?> GetBySkuAsync(string sku, CancellationToken ct)
        => Task.FromResult(_dashboard.Values.FirstOrDefault(r => r.Sku == sku));

    public Task<IReadOnlyList<InventoryDashboardRow>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<InventoryDashboardRow>>(_dashboard.Values.ToList());

    public Task TruncateAsync(CancellationToken ct)
    {
        _dashboard.Clear();
        _reservations.Clear();
        return Task.CompletedTask;
    }

    private sealed class UnitOfWork(InMemoryInventoryDashboardStore store) : IInventoryDashboardUnitOfWork
    {
        private NotificationEnvelope? _staged;

        public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(store.Checkpoints.GetValueOrDefault(projectionName));

        public Task CreateDashboardAsync(
            Guid inventoryId, string sku, DateTime lastUpdatedUtc, CancellationToken ct)
        {
            // ON CONFLICT DO NOTHING: a redelivered create keeps the first row.
            if (!store._dashboard.ContainsKey(inventoryId))
            {
                store._dashboard[inventoryId] = new InventoryDashboardRow(
                    InventoryId: inventoryId,
                    Sku: sku,
                    OnHandQuantity: 0,
                    ReservedQuantity: 0,
                    LastUpdatedUtc: lastUpdatedUtc);
            }
            return Task.CompletedTask;
        }

        public Task<string?> AdjustOnHandAsync(
            Guid inventoryId, int quantityDelta, DateTime lastUpdatedUtc, CancellationToken ct)
        {
            // Returns the row's sku, mirroring the adapter's RETURNING sku; null
            // when no row matched.
            if (store._dashboard.TryGetValue(inventoryId, out var existing))
            {
                store._dashboard[inventoryId] = existing with
                {
                    OnHandQuantity = existing.OnHandQuantity + quantityDelta,
                    LastUpdatedUtc = lastUpdatedUtc,
                };
                return Task.FromResult<string?>(existing.Sku);
            }
            return Task.FromResult<string?>(null);
        }

        public Task<string?> AdjustReservedAsync(
            Guid inventoryId, int reservedDelta, DateTime lastUpdatedUtc, CancellationToken ct)
        {
            // Returns the row's sku, mirroring the adapter's RETURNING sku; null
            // when no row matched.
            if (store._dashboard.TryGetValue(inventoryId, out var existing))
            {
                store._dashboard[inventoryId] = existing with
                {
                    ReservedQuantity = existing.ReservedQuantity + reservedDelta,
                    LastUpdatedUtc = lastUpdatedUtc,
                };
                return Task.FromResult<string?>(existing.Sku);
            }
            return Task.FromResult<string?>(null);
        }

        public Task InsertReservationAsync(InventoryReservationRow row, CancellationToken ct)
        {
            // ON CONFLICT DO NOTHING: a redelivered reserve keeps the first row.
            store._reservations.TryAdd((row.InventoryId, row.OrderId, row.LineId), row);
            return Task.CompletedTask;
        }

        public Task<InventoryReservationRow?> GetReservationAsync(
            Guid inventoryId, Guid orderId, Guid lineId, CancellationToken ct)
            => Task.FromResult(
                store._reservations.GetValueOrDefault((inventoryId, orderId, lineId)));

        public Task DeleteReservationAsync(
            Guid inventoryId, Guid orderId, Guid lineId, CancellationToken ct)
        {
            store._reservations.Remove((inventoryId, orderId, lineId));
            return Task.CompletedTask;
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
