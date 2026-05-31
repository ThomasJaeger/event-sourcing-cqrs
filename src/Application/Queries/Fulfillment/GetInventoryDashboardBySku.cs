using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;

namespace EventSourcingCqrs.Application.Queries.Fulfillment;

// Returns the inventory dashboard row for one SKU, or null when the SKU has no
// dashboard row. A thin pass-through over IInventoryDashboardStore.GetBySkuAsync;
// AddApplication's assembly scan registers the handler.
// Requires ViewInventory, which Support and Admin hold. Inventory is operational data with no owning
// customer, so there is no ownership filter; the permission gate is the whole enforcement.
public sealed record GetInventoryDashboardBySku(string Sku)
    : IQuery<InventoryDashboardRow?>, IAuthorizedQuery
{
    public static Permission RequiredPermission => Permission.ViewInventory;
}

public sealed class GetInventoryDashboardBySkuHandler
    : IQueryHandler<GetInventoryDashboardBySku, InventoryDashboardRow?>
{
    private readonly IInventoryDashboardStore _store;

    public GetInventoryDashboardBySkuHandler(IInventoryDashboardStore store)
    {
        _store = store;
    }

    public Task<InventoryDashboardRow?> HandleAsync(GetInventoryDashboardBySku query, CancellationToken ct)
        => _store.GetBySkuAsync(query.Sku, ct);
}
