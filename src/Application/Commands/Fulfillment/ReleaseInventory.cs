using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;

namespace EventSourcingCqrs.Application.Commands.Fulfillment;

public sealed record ReleaseInventory(
    Guid InventoryId,
    Guid LineId,
    string Reason) : IAuthorizedCommand
{
    public static Permission RequiredPermission => Permission.ReleaseInventory;
}

public sealed class ReleaseInventoryHandler : ICommandHandler<ReleaseInventory>
{
    private readonly IEventStoreRepository<Inventory> _repository;
    private readonly ICommandContextAccessor _accessor;

    public ReleaseInventoryHandler(
        IEventStoreRepository<Inventory> repository,
        ICommandContextAccessor accessor)
    {
        _repository = repository;
        _accessor = accessor;
    }

    public async Task HandleAsync(ReleaseInventory command, CancellationToken ct)
    {
        var inventory = await _repository.LoadAsync(command.InventoryId, ct)
            ?? throw new AggregateNotFoundException(command.InventoryId);
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        inventory.Release(command.LineId, command.Reason, utcNow);
        await _repository.SaveAsync(inventory, ct);
    }
}
