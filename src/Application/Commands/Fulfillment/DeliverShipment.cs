using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;

namespace EventSourcingCqrs.Application.Commands.Fulfillment;

public sealed record DeliverShipment(Guid ShipmentId) : IAuthorizedCommand
{
    public static Permission RequiredPermission => Permission.DeliverShipment;
}

public sealed class DeliverShipmentHandler : ICommandHandler<DeliverShipment>
{
    private readonly IEventStoreRepository<Shipment> _repository;
    private readonly ICommandContextAccessor _accessor;

    public DeliverShipmentHandler(
        IEventStoreRepository<Shipment> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(DeliverShipment command, CancellationToken ct)
    {
        var shipment = await _repository.LoadAsync(command.ShipmentId, ct)
            ?? throw new AggregateNotFoundException(command.ShipmentId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        shipment.Deliver(utcNow);
        await _repository.SaveAsync(shipment, ct);
    }
}
