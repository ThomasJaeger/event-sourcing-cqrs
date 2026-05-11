namespace EventSourcingCqrs.Domain.Abstractions;

public interface IEventStoreRepository<TAggregate>
    where TAggregate : AggregateRoot, new()
{
    Task<TAggregate?> LoadAsync(Guid id, CancellationToken ct);

    Task SaveAsync(TAggregate aggregate, CancellationToken ct);
}
