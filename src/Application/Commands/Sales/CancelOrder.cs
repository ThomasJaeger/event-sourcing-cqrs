using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;

namespace EventSourcingCqrs.Application.Commands.Sales;

// IssuedByUserId carries forward the aggregate's existing vocabulary; a future
// domain-level rename to IssuedByActorId would align with the commit-5
// ICommandContext.ActorId shape.
public sealed record CancelOrder(Guid OrderId, string Reason, Guid IssuedByUserId) : IAuthorizedCommand
{
    public static Permission RequiredPermission => Permission.CancelOrder;
}

public sealed class CancelOrderHandler : ICommandHandler<CancelOrder>
{
    private readonly IEventStoreRepository<Order> _repository;
    private readonly ICommandContextAccessor _accessor;

    public CancelOrderHandler(
        IEventStoreRepository<Order> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(CancelOrder command, CancellationToken ct)
    {
        var order = await _repository.LoadAsync(command.OrderId, ct)
            ?? throw new AggregateNotFoundException(command.OrderId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        order.Cancel(command.Reason, command.IssuedByUserId, utcNow);
        await _repository.SaveAsync(order, ct);
    }
}
