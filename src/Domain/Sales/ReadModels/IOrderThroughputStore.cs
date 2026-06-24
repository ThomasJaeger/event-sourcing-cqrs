using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// The order-throughput read model's persistence port. Lives in
// Domain.Sales.ReadModels because the throughput meter derives from the Sales
// order events (ADR 0008). Shape A: one bucket per (tenant, occurrence-second),
// the event type discarded. Mirrors IInventoryDashboardStore's store +
// unit-of-work split.
public interface IOrderThroughputStore
{
    Task<IOrderThroughputUnitOfWork> BeginAsync(CancellationToken ct);

    // Read path: every per-second bucket for the tenant the read is scoped to,
    // which the later throughput query and page read.
    Task<IReadOnlyList<OrderThroughputRow>> GetBucketsAsync(CancellationToken ct);
}

// One projection write: the per-second count increment and the checkpoint advance
// on one transaction. CommitAsync makes them durable together; DisposeAsync
// without a commit rolls them back. Mirrors IInventoryDashboardUnitOfWork.
public interface IOrderThroughputUnitOfWork : IAsyncDisposable
{
    Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct);

    // Count one order event into its occurrence-second bucket (Shape A: the second
    // is the whole key; the event type is discarded). The adapter UPSERTs the
    // (tenant, second) bucket with count + 1.
    Task IncrementSecondAsync(DateTime secondUtc, CancellationToken ct);

    // Prune the bucket set to the retention window: delete every bucket strictly
    // older than cutoffSecond, so the per-tenant row count stays bounded on write.
    Task PruneBeforeAsync(DateTime cutoffSecond, CancellationToken ct);

    // Stages a notification to publish atomically inside CommitAsync, the same
    // PublishOnCommit shape the other read-model units of work use. A unit of work
    // stages at most one notification; a second call throws.
    void PublishOnCommit(NotificationEnvelope envelope);

    Task CommitAsync(string projectionName, long position, CancellationToken ct);
}
