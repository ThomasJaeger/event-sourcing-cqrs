using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;

namespace EventSourcingCqrs.Application.Commands.Fulfillment;

public sealed record AdjustInventory(
    Guid InventoryId,
    int QuantityDelta,
    string Reason) : ICommand;

public sealed class AdjustInventoryHandler : ICommandHandler<AdjustInventory>
{
    private readonly IEventStoreRepository<Inventory> _repository;
    private readonly ICommandContextAccessor _accessor;

    public AdjustInventoryHandler(
        IEventStoreRepository<Inventory> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(AdjustInventory command, CancellationToken ct)
    {
        var inventory = await _repository.LoadAsync(command.InventoryId, ct)
            ?? throw new AggregateNotFoundException(command.InventoryId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        inventory.Adjust(command.QuantityDelta, command.Reason, utcNow);
        await _repository.SaveAsync(inventory, ct);
    }
}
