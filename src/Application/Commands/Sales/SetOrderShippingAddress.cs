using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Application.Commands.Sales;

public sealed record SetOrderShippingAddress(Guid OrderId, Address ShippingAddress) : IAuthorizedCommand
{
    public static Permission RequiredPermission => Permission.ManageOrderLines;
}

public sealed class SetOrderShippingAddressHandler : ICommandHandler<SetOrderShippingAddress>
{
    private readonly IEventStoreRepository<Order> _repository;
    private readonly ICommandContextAccessor _accessor;

    public SetOrderShippingAddressHandler(
        IEventStoreRepository<Order> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(SetOrderShippingAddress command, CancellationToken ct)
    {
        var order = await _repository.LoadAsync(command.OrderId, ct)
            ?? throw new AggregateNotFoundException(command.OrderId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        order.SetShippingAddress(command.ShippingAddress, utcNow);
        await _repository.SaveAsync(order, ct);
    }
}
