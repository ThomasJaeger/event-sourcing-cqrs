using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;

namespace EventSourcingCqrs.Application.Commands.Sales;

public sealed record DraftOrder(Guid OrderId, Guid CustomerId) : IAuthorizedCommand
{
    public static Permission RequiredPermission => Permission.DraftOrder;
}

// Special shape among the Order handlers: no LoadAsync, no
// AggregateNotFoundException. The factory creates a fresh aggregate, and
// SaveAsync appends with expectedVersion = 0; if the stream already exists,
// the event store throws ConcurrencyException, which is the right signal that
// "this OrderId is already drafted, send a different command."
public sealed class DraftOrderHandler : ICommandHandler<DraftOrder>
{
    private readonly IEventStoreRepository<Order> _repository;
    private readonly ICommandContextAccessor _accessor;

    public DraftOrderHandler(
        IEventStoreRepository<Order> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public Task HandleAsync(DraftOrder command, CancellationToken ct)
    {
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        var order = Order.Draft(command.OrderId, command.CustomerId, utcNow);
        return _repository.SaveAsync(order, ct);
    }
}
