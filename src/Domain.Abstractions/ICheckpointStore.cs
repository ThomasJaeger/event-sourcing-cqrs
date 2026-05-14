using System.Data.Common;

namespace EventSourcingCqrs.Domain.Abstractions;

// A projection's resume point. GetPositionAsync returns the global_position of
// the last event the projection processed, or 0 when it has never run.
// AdvanceAsync takes the caller's transaction so the checkpoint moves
// atomically with the read-model write made in the same handler. DbTransaction
// keeps the port engine-neutral; each adapter casts to its own transaction type.
public interface ICheckpointStore
{
    Task<long> GetPositionAsync(string projectionName, CancellationToken ct);

    Task AdvanceAsync(
        string projectionName,
        long position,
        DbTransaction transaction,
        CancellationToken ct);
}
