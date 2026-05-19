using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;

namespace EventSourcingCqrs.Application.Commands.Sales;

public sealed record RemoveOrderLine(Guid OrderId, Guid LineId) : ICommand;

public sealed class RemoveOrderLineHandler : ICommandHandler<RemoveOrderLine>
{
    private readonly IEventStoreRepository<Order> _repository;
    private readonly ICommandContextAccessor _accessor;

    public RemoveOrderLineHandler(
        IEventStoreRepository<Order> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(RemoveOrderLine command, CancellationToken ct)
    {
        var order = await _repository.LoadAsync(command.OrderId, ct)
            ?? throw new AggregateNotFoundException(command.OrderId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        order.RemoveLine(command.LineId, utcNow);
        await _repository.SaveAsync(order, ct);
    }
}
