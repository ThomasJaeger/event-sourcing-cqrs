using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Projections.Infrastructure;

// Composes the head-position port and the checkpoint store over the registered
// projection roster to report each projection's lag, head minus checkpoint.
//
// The read order is an invariant, not an incidental: every checkpoint is read before the head a
// lag is computed against. A projection only ever checkpoints at a position the log has already
// assigned, and the head never moves backwards, so a head read taken after a checkpoint read
// reports at least that checkpoint and the difference cannot come out negative. Reading the head
// first leaves a gap the other way: a projection that commits inside it is read as further along
// than the head already captured, and the operator sees a negative lag.
//
// The two reads still run on the two distinct port connections and share no snapshot; the reader
// assumes no shared database and issues no cross-schema JOIN. What the ordering buys is the sign,
// not precision. The head can advance between the checkpoint reads and the head read, so a lag can
// read slightly high. That is the conservative direction for a staleness tool: it overstates how
// far behind a projection is and never understates it.
//
// A negative that survives this ordering is a checkpoint past the end of the log, which is
// corruption. It renders as the negative it is, because a staleness tool that quietly clamps its
// own broken input is worse than one that shows an operator something impossible.
public sealed class ProjectionLagReader
{
    private readonly IEventStoreHeadPosition _headPosition;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IReadOnlyCollection<string> _projectionNames;

    public ProjectionLagReader(
        IEventStoreHeadPosition headPosition,
        ICheckpointStore checkpointStore,
        IProjectionRoster roster)
    {
        ArgumentNullException.ThrowIfNull(headPosition);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(roster);
        _headPosition = headPosition;
        _checkpointStore = checkpointStore;
        _projectionNames = roster.Names;
    }

    public async Task<IReadOnlyList<ProjectionLag>> ReadAsync(CancellationToken ct)
    {
        var checkpoints = new List<(string Name, long Position)>(_projectionNames.Count);
        foreach (var name in _projectionNames)
        {
            ct.ThrowIfCancellationRequested();
            checkpoints.Add((name, await _checkpointStore.GetPositionAsync(name, ct)));
        }

        // The head is global and identical for every projection, so read it once, and read it last
        // so that it is at least as recent as every checkpoint above.
        var head = await _headPosition.GetHeadPositionAsync(ct);

        var rows = new List<ProjectionLag>(checkpoints.Count);
        foreach (var (name, checkpoint) in checkpoints)
        {
            rows.Add(new ProjectionLag(name, head, checkpoint, head - checkpoint));
        }
        return rows;
    }
}
