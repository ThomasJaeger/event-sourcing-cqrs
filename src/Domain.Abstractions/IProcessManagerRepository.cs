namespace EventSourcingCqrs.Domain.Abstractions;

// Persistence for process managers, parallel to IEventStoreRepository<TAggregate>
// (ADR 0012). A PM is constructed with its StreamId, so the factory is the
// construction path rather than a parameterless constructor; both load methods
// take it. The factory builds the instance that LoadFromHistory replays onto.
public interface IProcessManagerRepository<TPm>
    where TPm : ProcessManager
{
    // Returns null when the stream is empty (no events). The factory is invoked
    // only when events exist, to build the instance for replay.
    Task<TPm?> LoadAsync(
        StreamId streamId,
        Func<StreamId, TPm> factory,
        CancellationToken ct);

    // Loads the PM if its stream exists, otherwise returns a fresh instance
    // from the factory. Workflow-initiating-event handlers use this.
    Task<TPm> LoadOrNewAsync(
        StreamId streamId,
        Func<StreamId, TPm> factory,
        CancellationToken ct);

    Task SaveAsync(TPm pm, CancellationToken ct);
}
