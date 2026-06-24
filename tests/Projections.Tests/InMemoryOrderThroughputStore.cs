using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;

namespace EventSourcingCqrs.Projections.Tests;

// In-memory IOrderThroughputStore for the projection tests, mirroring
// InMemoryInventoryDashboardStore. Increments apply immediately and CommitAsync
// records the checkpoint and flushes the staged notification, because the
// projection always commits and the tests assert on the committed result. The
// store is functional test infrastructure: an empty buckets set means the
// projection never counted, which is exactly what the RED test asserts against.
internal sealed class InMemoryOrderThroughputStore : IOrderThroughputStore
{
    private readonly Dictionary<DateTime, long> _buckets = [];

    // Exposed so tests can assert the checkpoint advanced with the write.
    public Dictionary<string, long> Checkpoints { get; } = [];

    // Records each committed unit of work's staged notification, flushed on
    // CommitAsync so an uncommitted unit stages nothing the tests can observe.
    public List<NotificationEnvelope> StagedNotifications { get; } = [];

    public Task<IOrderThroughputUnitOfWork> BeginAsync(CancellationToken ct)
        => Task.FromResult<IOrderThroughputUnitOfWork>(new UnitOfWork(this));

    public Task<IReadOnlyList<OrderThroughputRow>> GetBucketsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<OrderThroughputRow>>(
            _buckets.Select(b => new OrderThroughputRow(b.Key, b.Value)).ToList());

    private sealed class UnitOfWork(InMemoryOrderThroughputStore store) : IOrderThroughputUnitOfWork
    {
        private NotificationEnvelope? _staged;

        public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(store.Checkpoints.GetValueOrDefault(projectionName));

        public Task IncrementSecondAsync(DateTime secondUtc, CancellationToken ct)
        {
            store._buckets[secondUtc] = store._buckets.GetValueOrDefault(secondUtc) + 1;
            return Task.CompletedTask;
        }

        public Task PruneBeforeAsync(DateTime cutoffSecond, CancellationToken ct)
        {
            // Delete every bucket strictly older than the cutoff. ToList so the
            // dictionary is not mutated mid-enumeration.
            foreach (var second in store._buckets.Keys.Where(k => k < cutoffSecond).ToList())
            {
                store._buckets.Remove(second);
            }
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
            if (_staged is not null)
            {
                store.StagedNotifications.Add(_staged);
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
