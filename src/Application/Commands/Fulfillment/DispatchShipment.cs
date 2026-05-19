using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;

namespace EventSourcingCqrs.Application.Commands.Fulfillment;

public sealed record DispatchShipment(
    Guid ShipmentId,
    string CarrierReference) : ICommand;

public sealed class DispatchShipmentHandler : ICommandHandler<DispatchShipment>
{
    private readonly IEventStoreRepository<Shipment> _repository;
    private readonly ICommandContextAccessor _accessor;

    public DispatchShipmentHandler(
        IEventStoreRepository<Shipment> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(DispatchShipment command, CancellationToken ct)
    {
        var shipment = await _repository.LoadAsync(command.ShipmentId, ct)
            ?? throw new AggregateNotFoundException(command.ShipmentId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        shipment.Dispatch(command.CarrierReference, utcNow);
        await _repository.SaveAsync(shipment, ct);
    }
}
