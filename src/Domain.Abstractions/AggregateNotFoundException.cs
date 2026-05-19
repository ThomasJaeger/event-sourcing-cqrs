namespace EventSourcingCqrs.Domain.Abstractions;

public sealed class AggregateNotFoundException : Exception
{
    public Guid AggregateId { get; }

    public AggregateNotFoundException(Guid aggregateId)
        : base($"Aggregate {aggregateId} was not found.")
    {
        AggregateId = aggregateId;
    }
}
