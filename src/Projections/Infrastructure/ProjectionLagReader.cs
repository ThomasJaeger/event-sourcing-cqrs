using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Projections.Infrastructure;

// Composes the head-position port and the checkpoint store over the registered
// projection roster to report each projection's lag, head minus checkpoint. The
// head read and the checkpoint read run on the two distinct port connections, so
// the reader assumes no shared database and issues no cross-schema JOIN.
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
        // The head is global and identical for every projection, so read it once.
        var head = await _headPosition.GetHeadPositionAsync(ct);

        var rows = new List<ProjectionLag>(_projectionNames.Count);
        foreach (var name in _projectionNames)
        {
            ct.ThrowIfCancellationRequested();
            var checkpoint = await _checkpointStore.GetPositionAsync(name, ct);
            rows.Add(new ProjectionLag(name, head, checkpoint, head - checkpoint));
        }
        return rows;
    }
}
