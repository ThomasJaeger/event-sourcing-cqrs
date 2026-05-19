using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;

namespace EventSourcingCqrs.Application.Commands.Sales;

public sealed record ShipOrder(Guid OrderId, string Carrier, string TrackingNumber) : ICommand;

public sealed class ShipOrderHandler : ICommandHandler<ShipOrder>
{
    private readonly IEventStoreRepository<Order> _repository;
    private readonly ICommandContextAccessor _accessor;

    public ShipOrderHandler(
        IEventStoreRepository<Order> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(ShipOrder command, CancellationToken ct)
    {
        var order = await _repository.LoadAsync(command.OrderId, ct)
            ?? throw new AggregateNotFoundException(command.OrderId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        order.Ship(command.Carrier, command.TrackingNumber, utcNow);
        await _repository.SaveAsync(order, ct);
    }
}
