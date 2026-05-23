using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;

namespace EventSourcingCqrs.Application.Queries.Fulfillment;

// Returns the inventory dashboard row for one SKU, or null when the SKU has no
// dashboard row. A thin pass-through over IInventoryDashboardStore.GetBySkuAsync;
// AddApplication's assembly scan registers the handler.
public sealed record GetInventoryDashboardBySku(string Sku) : IQuery<InventoryDashboardRow?>;

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
