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

    protected abstract void Apply(IDomainEvent @event);

    public IReadOnlyList<IDomainEvent> DequeueUncommittedEvents()
    {
        var events = _uncommitted.ToArray();
        _uncommitted.Clear();
        return events;
    }
}
