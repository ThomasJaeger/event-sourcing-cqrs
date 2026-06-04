using System.Data.Common;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Projections.Infrastructure;

// The checkpoint store a per-tenant rebuild runs the projection over, so the replay is
// checkpoint-neutral by construction. Both reads return a floor below the first real
// global_position (PostgreSQL IDENTITY starts at 1), so the handler's
// "checkpoint >= position" skip never fires and the reset-emptied rows re-populate; the
// advance is a no-op, so the shared global projection_checkpoints row is never written
// and global catch-up resumes from where it was. The real checkpoint store still holds
// the live position; the rebuilder reads the replay ceiling off it and never advances it.
public sealed class RebuildModeCheckpointStore : ICheckpointStore
{
    // Below the first real global_position (1), so no replayed event is ever skipped.
    private const long Floor = -1;

    public Task<long> GetPositionAsync(string projectionName, CancellationToken ct)
        => Task.FromResult(Floor);

    public Task<long> GetPositionAsync(
        string projectionName, DbTransaction transaction, CancellationToken ct)
        => Task.FromResult(Floor);

    public Task AdvanceAsync(
        string projectionName, long position, DbTransaction transaction, CancellationToken ct)
        => Task.CompletedTask;
}
