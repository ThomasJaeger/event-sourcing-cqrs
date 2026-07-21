namespace EventSourcingCqrs.Domain.Abstractions;

public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> _uncommitted = [];

    public Guid Id { get; protected set; }

    public int Version { get; private set; }

    protected void Raise(IDomainEvent @event)
    {
        Apply(@event);
        _uncommitted.Add(@event);
        Version++;
    }

    public void ApplyHistoric(IDomainEvent @event)
    {
        Apply(@event);
        Version++;
    }

    // The seam a snapshot restore uses to seat the aggregate at the version its memento was taken at,
    // so the rest of the load replays only the tail from there. Guarded to a pristine instance:
    // teleporting the version onto a live aggregate, one that has already applied events or holds
    // uncommitted work, would overwrite the concurrency token the next append checks its expected
    // version against, and let a stale write land as if it were current.
    protected void RestoreVersion(int version)
    {
        if (Version != 0 || _uncommitted.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot restore version onto a non-pristine aggregate: Version is {Version} with " +
                $"{_uncommitted.Count} uncommitted event(s). Restore only onto a fresh instance.");
        }
        Version = version;
    }

    protected abstract void Apply(IDomainEvent @event);

    public IReadOnlyList<IDomainEvent> DequeueUncommittedEvents()
    {
        var events = _uncommitted.ToArray();
        _uncommitted.Clear();
        return events;
    }
}
