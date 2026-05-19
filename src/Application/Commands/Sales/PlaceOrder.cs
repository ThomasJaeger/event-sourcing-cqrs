using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;

namespace EventSourcingCqrs.Application.Commands.Sales;

public sealed record PlaceOrder(Guid OrderId) : ICommand;

public sealed class PlaceOrderHandler : ICommandHandler<PlaceOrder>
{
    private readonly IEventStoreRepository<Order> _repository;
    private readonly ICommandContextAccessor _accessor;

    public PlaceOrderHandler(
        IEventStoreRepository<Order> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(PlaceOrder command, CancellationToken ct)
    {
        var order = await _repository.LoadAsync(command.OrderId, ct)
            ?? throw new AggregateNotFoundException(command.OrderId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        order.Place(utcNow);
        await _repository.SaveAsync(order, ct);
    }
}
