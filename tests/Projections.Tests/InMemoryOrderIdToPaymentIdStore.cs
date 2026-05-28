using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.ReadModels;

namespace EventSourcingCqrs.Projections.Tests;

// In-memory IOrderIdToPaymentIdStore for OrderIdToPaymentIdProjectionTests,
// mirroring InMemorySkuToInventoryIdStore. Writes apply immediately; CommitAsync
// records the checkpoint. RecordCount lets the redelivery-skip test assert the
// early-return path bailed before touching the unit of work, not just that the
// mapping ended up unchanged (which would also hold under TryAdd's
// first-write-wins).
internal sealed class InMemoryOrderIdToPaymentIdStore : IOrderIdToPaymentIdStore
{
    private readonly Dictionary<Guid, Guid> _mappings = [];

    public Dictionary<string, long> Checkpoints { get; } = [];

    // Records each committed unit of work's staged notification. No v1 consumer
    // subscribes to this lookup, so the projection never stages; the list stays
    // empty and a test asserts that.
    public List<NotificationEnvelope> StagedNotifications { get; } = [];

    public int RecordCount { get; private set; }

    public Task<IOrderIdToPaymentIdUnitOfWork> BeginAsync(CancellationToken ct)
        => Task.FromResult<IOrderIdToPaymentIdUnitOfWork>(new UnitOfWork(this));

    public Task<Guid?> GetPaymentIdAsync(Guid orderId, CancellationToken ct)
        => Task.FromResult(_mappings.TryGetValue(orderId, out var id) ? id : (Guid?)null);

    public Task TruncateAsync(CancellationToken ct)
    {
        _mappings.Clear();
        return Task.CompletedTask;
    }

    private sealed class UnitOfWork(InMemoryOrderIdToPaymentIdStore store) : IOrderIdToPaymentIdUnitOfWork
    {
        private NotificationEnvelope? _staged;

        public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(store.Checkpoints.GetValueOrDefault(projectionName));

        public Task RecordAsync(Guid orderId, Guid paymentId, CancellationToken ct)
        {
            store.RecordCount++;
            // ON CONFLICT DO NOTHING: a redelivered mapping leaves the first.
            store._mappings.TryAdd(orderId, paymentId);
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
