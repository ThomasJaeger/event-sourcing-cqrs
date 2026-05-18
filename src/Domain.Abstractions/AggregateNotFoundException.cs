namespace EventSourcingCqrs.Domain.Abstractions;

public sealed class AggregateNotFoundException : Exception
{
    public string AggregateType { get; }
    public Guid AggregateId { get; }

    public AggregateNotFoundException(string aggregateType, Guid aggregateId)
        : base($"{aggregateType} with id {aggregateId} was not found.")
    {
        AggregateType = aggregateType;
        AggregateId = aggregateId;
    }
}
