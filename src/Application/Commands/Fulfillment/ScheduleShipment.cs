using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Application.Commands.Fulfillment;

public sealed record ScheduleShipment(
    Guid ShipmentId,
    Guid OrderId,
    Address Destination,
    IReadOnlyList<ShipmentLine> Lines) : ICommand;

// Special shape among the Shipment handlers: no LoadAsync, no
// AggregateNotFoundException. The factory creates a fresh aggregate, and
// SaveAsync appends with expectedVersion = 0; if the stream already exists,
// the event store throws ConcurrencyException, which is the right signal
// that "this ShipmentId is already scheduled, send a different command."
// Mirrors CreateInventoryHandler's shape.
public sealed class ScheduleShipmentHandler : ICommandHandler<ScheduleShipment>
{
    private readonly IEventStoreRepository<Shipment> _repository;
    private readonly ICommandContextAccessor _accessor;

    public ScheduleShipmentHandler(
        IEventStoreRepository<Shipment> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public Task HandleAsync(ScheduleShipment command, CancellationToken ct)
    {
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        var shipment = Shipment.Schedule(
            command.ShipmentId,
            command.OrderId,
            command.Destination,
            command.Lines,
            utcNow);
        return _repository.SaveAsync(shipment, ct);
    }
}
