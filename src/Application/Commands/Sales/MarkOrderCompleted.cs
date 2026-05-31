using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;

namespace EventSourcingCqrs.Application.Commands.Sales;

// Dispatched by the OrderFulfillment process manager when the shipment is
// delivered (Decision 13). A bounded-context command Sales owns, so it lives in
// Application alongside the other Order commands, unlike the PM-internal timeout
// commands that live in ProcessManagers.
public sealed record MarkOrderCompleted(Guid OrderId) : IAuthorizedCommand
{
    public static Permission RequiredPermission => Permission.MarkOrderCompleted;
}

public sealed class MarkOrderCompletedHandler : ICommandHandler<MarkOrderCompleted>
{
    private readonly IEventStoreRepository<Order> _repository;
    private readonly ICommandContextAccessor _accessor;

    public MarkOrderCompletedHandler(
        IEventStoreRepository<Order> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(MarkOrderCompleted command, CancellationToken ct)
    {
        var order = await _repository.LoadAsync(command.OrderId, ct)
            ?? throw new AggregateNotFoundException(command.OrderId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        order.Complete(utcNow);
        await _repository.SaveAsync(order, ct);
    }
}
