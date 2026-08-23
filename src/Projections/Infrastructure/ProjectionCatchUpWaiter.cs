using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Projections.Infrastructure;

// Waits until every projection that subscribes to a written event has checkpointed at or past
// the head of the log. A caller that has just dispatched commands knows which event types it
// caused; this turns that into a completion condition it can wait on instead of sleeping.
//
// Why the wait is per event type rather than over the whole roster. A projection advances only
// on events it handles, so a projection subscribing to none of the written types never moves and
// waiting on it would never return. The container is the only place that map is readable:
// resolving the closed IEventHandler<TEvent> yields the projections registered for it, and every
// registered event handler in this solution is a projection. The reverse direction, from a
// projection to its event types, no container query answers.
//
// Read ordering follows ProjectionLagReader: every checkpoint first, the head last. A projection
// only checkpoints at a position the log has already assigned and the head never moves backwards,
// so a head read taken after the checkpoints is at least as recent as all of them and a
// projection can never read as further along than the head it is compared against.
//
// The deadline is wall-clock arithmetic over the injected TimeProvider rather than a timer, so a
// test can expire the bound by advancing the clock without spending the budget in real time.
public sealed class ProjectionCatchUpWaiter
{
    private readonly IEventStoreHeadPosition _headPosition;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;

    public ProjectionCatchUpWaiter(
        IEventStoreHeadPosition headPosition,
        ICheckpointStore checkpointStore,
        IServiceProvider services,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(headPosition);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _headPosition = headPosition;
        _checkpointStore = checkpointStore;
        _services = services;
        _timeProvider = timeProvider;
    }

    // Returns the projections it waited on, which is the derived set rather than the roster.
    // Throws TimeoutException when the bound expires with a projection still behind.
    public async Task<IReadOnlyCollection<string>> WaitForCatchUpAsync(
        IReadOnlyCollection<Type> eventTypes,
        TimeSpan budget,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);

        var names = DeriveProjectionNames(eventTypes);

        // Nothing subscribes to what was written, so there is nothing to be behind on. Returning
        // here is what keeps the ports untouched in that case.
        if (names.Count == 0)
        {
            return names;
        }

        var deadline = _timeProvider.GetUtcNow() + budget;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var positions = new List<(string Name, long Position)>(names.Count);
            foreach (var name in names)
            {
                positions.Add((name, await _checkpointStore.GetPositionAsync(name, ct)));
            }

            // Read once, and last, so it is at least as recent as every checkpoint above.
            var head = await _headPosition.GetHeadPositionAsync(ct);

            var behind = positions.Where(p => p.Position < head).ToList();
            if (behind.Count == 0)
            {
                return names;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                var detail = string.Join(", ", behind.Select(b => $"{b.Name} at {b.Position}"));
                throw new TimeoutException(
                    $"Projections did not reach global position {head} within {budget}: {detail}.");
            }

            await Task.Delay(pollInterval, _timeProvider, ct);
        }
    }

    // One name per projection, deduplicated across the event types, because a projection that
    // subscribes to two of them is still one thing to wait on.
    private List<string> DeriveProjectionNames(IReadOnlyCollection<Type> eventTypes)
    {
        var names = new List<string>();
        foreach (var eventType in eventTypes)
        {
            var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
            foreach (var handler in _services.GetServices(handlerType))
            {
                if (handler is IProjection projection && !names.Contains(projection.Name))
                {
                    names.Add(projection.Name);
                }
            }
        }
        return names;
    }
}
