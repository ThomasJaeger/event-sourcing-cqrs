namespace EventSourcingCqrs.Domain.Abstractions;

public static class StreamNames
{
    public static string ForAggregate<TAggregate>(Guid aggregateId)
        where TAggregate : AggregateRoot
        => $"{typeof(TAggregate).Name}-{aggregateId:N}";

    public static string ForAggregate(string type, Guid aggregateId)
        => $"{type}-{aggregateId:N}";

    public static string CategoryFor<TAggregate>()
        where TAggregate : AggregateRoot
        => $"$ce-{typeof(TAggregate).Name}";

    public static string ForPartition<TAggregate>(Guid aggregateId, string partition)
        where TAggregate : AggregateRoot
        => $"{typeof(TAggregate).Name}-{aggregateId:N}-{partition}";

    public static string SummaryFor<TAggregate>(Guid aggregateId)
        where TAggregate : AggregateRoot
        => $"{typeof(TAggregate).Name}-{aggregateId:N}-summary";
}
