using System.Data.Common;

namespace EventSourcingCqrs.Domain.Abstractions;

// A projection's resume point. The non-transactional GetPositionAsync returns
// the global_position of the last event the projection processed, or 0 when it
// has never run; it is the catch-up service's pre-replay read. The transactional
// overload serves handler-level idempotency: a projection reads the checkpoint
// inside its own write transaction and skips events at or below it, so an
// at-least-once redelivery is a no-op without touching the read model.
// AdvanceAsync takes the caller's transaction so the checkpoint moves
// atomically with the read-model write made in the same handler. DbTransaction
// keeps the port engine-neutral; each adapter casts to its own transaction type.
public interface ICheckpointStore
{
    Task<long> GetPositionAsync(string projectionName, CancellationToken ct);

    Task<long> GetPositionAsync(
        string projectionName,
        DbTransaction transaction,
        CancellationToken ct);

    Task AdvanceAsync(
        string projectionName,
        long position,
        DbTransaction transaction,
        CancellationToken ct);

    // Advances the checkpoint with no surrounding transaction, for a writer that has no read-model
    // write to move it atomically with: the KurrentDB subscription dispatch service is the first, which
    // advances its own $all position after a dispatch and across filtered stretches, outside any
    // projection's unit of work. The store opens its own connection; the same GREATEST upsert keeps it
    // idempotent, so an at-least-once redelivery carrying an already-persisted position is a no-op.
    Task AdvanceAsync(
        string projectionName,
        long position,
        CancellationToken ct);
}
