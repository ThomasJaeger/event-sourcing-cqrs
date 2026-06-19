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
    private readonly IReadOnlyList<IProjection> _projections;

    public ProjectionLagReader(
        IEventStoreHeadPosition headPosition,
        ICheckpointStore checkpointStore,
        IEnumerable<IProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(headPosition);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(projections);
        _headPosition = headPosition;
        _checkpointStore = checkpointStore;
        _projections = projections.ToList();
    }

    public async Task<IReadOnlyList<ProjectionLag>> ReadAsync(CancellationToken ct)
    {
        // The head is global and identical for every projection, so read it once.
        var head = await _headPosition.GetHeadPositionAsync(ct);

        var rows = new List<ProjectionLag>(_projections.Count);
        foreach (var projection in _projections)
        {
            ct.ThrowIfCancellationRequested();
            var checkpoint = await _checkpointStore.GetPositionAsync(projection.Name, ct);
            rows.Add(new ProjectionLag(projection.Name, head, checkpoint, head - checkpoint));
        }
        return rows;
    }
}
