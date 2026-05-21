namespace EventSourcingCqrs.Domain.Abstractions;

// Base class for event-sourced process managers, separate from AggregateRoot
// per ADR 0012. A PM consumes events from aggregates, records its own
// transitions as IProcessManagerEvent instances on a dedicated stream, and
// rehydrates by replaying that stream. The vocabulary diverges from
// AggregateRoot deliberately: RecordTransition rather than Raise, and the
// GetUncommittedEvents/MarkCommitted split rather than the aggregate's single
// DequeueUncommittedEvents, so the two families read differently at the call
// site. The StreamId is the typed ADR 0011 identifier, supplied at
// construction and immutable; the repository's factory passes it in.
public abstract class ProcessManager
{
    private readonly List<IProcessManagerEvent> _uncommitted = [];

    protected ProcessManager(StreamId streamId)
    {
        StreamId = streamId;
    }

    public StreamId StreamId { get; }

    public int Version { get; private set; }

    protected void RecordTransition(IProcessManagerEvent @event)
    {
        Apply(@event);
        _uncommitted.Add(@event);
        Version++;
    }

    public void LoadFromHistory(IEnumerable<IProcessManagerEvent> history)
    {
        foreach (var @event in history)
        {
            Apply(@event);
            Version++;
        }
    }

    public IReadOnlyList<IProcessManagerEvent> GetUncommittedEvents() => _uncommitted;

    // Cleared by the repository only after a successful append, so a persist
    // failure leaves the buffer intact for a retry. The deliberate contrast
    // with AggregateRoot.DequeueUncommittedEvents, which returns and clears in
    // one call (ADR 0012).
    public void MarkCommitted() => _uncommitted.Clear();

    protected abstract void Apply(IProcessManagerEvent @event);
}
