using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;

namespace EventSourcingCqrs.Application.Commands.Sales;

// IssuedByUserId carries forward the aggregate's existing vocabulary; a future
// domain-level rename to IssuedByActorId would align with the commit-5
// ICommandContext.ActorId shape.
public sealed record CancelOrder(Guid OrderId, string Reason, Guid IssuedByUserId) : IAuthorizedCommand, IOrderWorkflowCommand
{
    public static Permission RequiredPermission => Permission.CancelOrder;
}

public sealed class CancelOrderHandler : ICommandHandler<CancelOrder>
{
    private readonly IEventStoreRepository<Order> _repository;
    private readonly ICommandContextAccessor _accessor;
    private readonly IOrderCancellationGuard _cancellationGuard;

    public CancelOrderHandler(
        IEventStoreRepository<Order> repository,
        ICommandContextAccessor accessor, IOrderCancellationGuard cancellationGuard)
    {
        _repository = repository;
        _accessor = accessor;
        _cancellationGuard = cancellationGuard;
    }

    public async Task HandleAsync(CancelOrder command, CancellationToken ct)
    {
        var order = await _repository.LoadAsync(command.OrderId, ct)
            ?? throw new AggregateNotFoundException(command.OrderId);
        OrderCommandAuthorization.EnsureOwnership<CancelOrder>(order.CustomerId, _accessor.Current);
        if (order.Status == OrderStatus.Cancelled
            && _accessor.Current?.AuthorizationMode == DispatchAuthorizationMode.SystemActor) return;
        await _cancellationGuard.EnsureCanCancelAsync(command.OrderId, ct);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        var actor = _accessor.Current is { AuthorizationMode: not DispatchAuthorizationMode.None } context
            ? context.ActorId : command.IssuedByUserId;
        order.Cancel(command.Reason, actor, utcNow);
        await _repository.SaveAsync(order, ct);
    }
}
