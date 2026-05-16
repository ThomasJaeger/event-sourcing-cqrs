using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventSourcingCqrs.Projections.Infrastructure;

// Catches every registered IProjection up to the current end of the events
// table on host startup, before steady-state delivery begins. Runs in
// IHostedLifecycleService.StartingAsync, which the .NET hosted-lifecycle
// pipeline invokes before any IHostedService.StartAsync (including the
// OutboxProcessor BackgroundService). Once StartingAsync returns, the
// dispatcher's live tail takes over and the catch-up never runs again
// during this host's lifetime.
//
// Sequential across projections. A future four-projection world earns
// parallel catch-up if startup cost becomes measurable; for one projection
// in v1, sequence keeps the log timeline clean and avoids contending for
// the same connection pool out of the gate.
public sealed class ProjectionStartupCatchUpService : IHostedLifecycleService
{
    private readonly IReadOnlyList<IProjection> _projections;
    private readonly IEventStore _eventStore;
    private readonly ICheckpointStore _checkpointStore;
    private readonly ILogger<ProjectionStartupCatchUpService> _logger;

    public ProjectionStartupCatchUpService(
        IEnumerable<IProjection> projections,
        IEventStore eventStore,
        ICheckpointStore checkpointStore,
        ILogger<ProjectionStartupCatchUpService> logger)
    {
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(logger);
        _projections = projections.ToList();
        _eventStore = eventStore;
        _checkpointStore = checkpointStore;
        _logger = logger;
    }

    public async Task StartingAsync(CancellationToken ct)
    {
        foreach (var projection in _projections)
        {
            ct.ThrowIfCancellationRequested();
            var fromPosition = await _checkpointStore.GetPositionAsync(projection.Name, ct);
            _logger.LogInformation(
                "Catching up projection {Projection} from global position {Position}",
                projection.Name, fromPosition);
            await new ProjectionReplayer(_eventStore, projection).ReplayAsync(fromPosition, ct);
        }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
}
