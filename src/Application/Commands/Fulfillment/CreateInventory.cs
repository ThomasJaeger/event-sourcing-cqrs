using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;

namespace EventSourcingCqrs.Application.Commands.Fulfillment;

public sealed record CreateInventory(Guid InventoryId, string Sku) : ICommand;

// Special shape among the Inventory handlers: no LoadAsync, no
// AggregateNotFoundException. The factory creates a fresh aggregate, and
// SaveAsync appends with expectedVersion = 0; if the stream already exists,
// the event store throws ConcurrencyException, which is the right signal
// that "this InventoryId is already created, send a different command."
// Mirrors DraftOrderHandler's shape.
public sealed class CreateInventoryHandler : ICommandHandler<CreateInventory>
{
    private readonly IEventStoreRepository<Inventory> _repository;
    private readonly ICommandContextAccessor _accessor;

    public CreateInventoryHandler(
        IEventStoreRepository<Inventory> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public Task HandleAsync(CreateInventory command, CancellationToken ct)
    {
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        var inventory = Inventory.Create(command.InventoryId, command.Sku, utcNow);
        return _repository.SaveAsync(inventory, ct);
    }
}
