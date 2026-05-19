using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;

namespace EventSourcingCqrs.Application.Commands.Fulfillment;

public sealed record ReserveInventory(
    Guid InventoryId,
    Guid OrderId,
    Guid LineId,
    int Quantity) : ICommand;

public sealed class ReserveInventoryHandler : ICommandHandler<ReserveInventory>
{
    private readonly IEventStoreRepository<Inventory> _repository;
    private readonly ICommandContextAccessor _accessor;

    public ReserveInventoryHandler(
        IEventStoreRepository<Inventory> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(ReserveInventory command, CancellationToken ct)
    {
        var inventory = await _repository.LoadAsync(command.InventoryId, ct)
            ?? throw new AggregateNotFoundException(command.InventoryId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        inventory.Reserve(command.OrderId, command.LineId, command.Quantity, utcNow);
        await _repository.SaveAsync(inventory, ct);
    }
}
