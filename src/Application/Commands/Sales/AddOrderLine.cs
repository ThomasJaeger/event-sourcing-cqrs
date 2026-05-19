using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Application.Commands.Sales;

public sealed record AddOrderLine(
    Guid OrderId,
    Guid LineId,
    string Sku,
    int Quantity,
    Money UnitPrice) : ICommand;

public sealed class AddOrderLineHandler : ICommandHandler<AddOrderLine>
{
    private readonly IEventStoreRepository<Order> _repository;
    private readonly ICommandContextAccessor _accessor;

    public AddOrderLineHandler(
        IEventStoreRepository<Order> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(AddOrderLine command, CancellationToken ct)
    {
        var order = await _repository.LoadAsync(command.OrderId, ct)
            ?? throw new AggregateNotFoundException(command.OrderId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        order.AddLine(command.LineId, command.Sku, command.Quantity, command.UnitPrice, utcNow);
        await _repository.SaveAsync(order, ct);
    }
}
