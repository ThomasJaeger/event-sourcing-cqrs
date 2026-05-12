namespace EventSourcingCqrs.Domain.Abstractions;

public interface IMessageDispatcher
{
    Task DispatchAsync(OutboxMessage message, CancellationToken ct);
}
