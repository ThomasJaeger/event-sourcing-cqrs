using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;

namespace EventSourcingCqrs.Application.Commands.Fulfillment;

public sealed record CreateInventory(Guid InventoryId, string Sku) : IAuthorizedCommand
{
    public static Permission RequiredPermission => Permission.CreateInventory;
}

// Special shape among the Inventory handlers: no LoadAsync, no
// AggregateNotFoundException. The factory creates a fresh aggregate, and
// SaveAsync appends with expectedVersion = 0; if the stream already exists,
// the event store throws ConcurrencyException, which is the right signal
// that "this InventoryId is already created, send a different command."
// A durable SKU claim precedes the append so two ids cannot create the same tenant's SKU.
public sealed class CreateInventoryHandler : ICommandHandler<CreateInventory>
{
    private readonly IEventStoreRepository<Inventory> _repository;
    private readonly ICommandContextAccessor _accessor;
    private readonly ICurrentTenantAccessor _tenantAccessor;
    private readonly IInventorySkuRegistry _skuRegistry;

    public CreateInventoryHandler(
        IEventStoreRepository<Inventory> repository,
        ICommandContextAccessor accessor,
        ICurrentTenantAccessor tenantAccessor,
        IInventorySkuRegistry skuRegistry)
    {
        _repository = repository;
        _accessor = accessor;
        _tenantAccessor = tenantAccessor;
        _skuRegistry = skuRegistry;
    }

    public async Task HandleAsync(CreateInventory command, CancellationToken ct)
    {
        var utcNow = (_accessor.Current ?? CommandContext.System).UtcNow().UtcDateTime;
        var inventory = Inventory.Create(command.InventoryId, command.Sku, utcNow);
        var tenant = _tenantAccessor.Current
            ?? (_accessor.Current is null ? WellKnownTenants.Default : throw new MissingTenantContextException());
        await _skuRegistry.ClaimAsync(tenant, command.InventoryId, command.Sku, ct);
        await _repository.SaveAsync(inventory, ct);
    }
}
