using EventSourcingCqrs.Domain.Fulfillment.ReadModels;

namespace EventSourcingCqrs.Projections.Tests;

// In-memory ISkuToInventoryIdStore for SkuToInventoryIdProjectionTests, mirroring
// InMemoryOrderListStore. Writes apply immediately; CommitAsync records the
// checkpoint. RecordCount lets the redelivery-skip test assert the early-return
// path bailed before touching the unit of work, not just that the mapping ended
// up unchanged (which would also hold under TryAdd's first-write-wins).
internal sealed class InMemorySkuToInventoryIdStore : ISkuToInventoryIdStore
{
    private readonly Dictionary<string, Guid> _mappings = [];

    public Dictionary<string, long> Checkpoints { get; } = [];

    public int RecordCount { get; private set; }

    public Task<ISkuToInventoryIdUnitOfWork> BeginAsync(CancellationToken ct)
        => Task.FromResult<ISkuToInventoryIdUnitOfWork>(new UnitOfWork(this));

    public Task<Guid?> GetInventoryIdAsync(string sku, CancellationToken ct)
        => Task.FromResult(_mappings.TryGetValue(sku, out var id) ? id : (Guid?)null);

    public Task TruncateAsync(CancellationToken ct)
    {
        _mappings.Clear();
        return Task.CompletedTask;
    }

    private sealed class UnitOfWork(InMemorySkuToInventoryIdStore store) : ISkuToInventoryIdUnitOfWork
    {
        public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(store.Checkpoints.GetValueOrDefault(projectionName));

        public Task RecordAsync(string sku, Guid inventoryId, CancellationToken ct)
        {
            store.RecordCount++;
            // ON CONFLICT DO NOTHING: a redelivered mapping leaves the first.
            store._mappings.TryAdd(sku, inventoryId);
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
