using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;

namespace EventSourcingCqrs.Application.Queries.Fulfillment;

// Returns every inventory dashboard row, ordered by SKU, for the full-list
// dashboard view. A thin pass-through over IInventoryDashboardStore.GetAllAsync;
// AddApplication's assembly scan registers the handler.
public sealed record GetAllInventoryDashboard() : IQuery<IReadOnlyList<InventoryDashboardRow>>;

public sealed class GetAllInventoryDashboardHandler
    : IQueryHandler<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>
{
    private readonly IInventoryDashboardStore _store;

    public GetAllInventoryDashboardHandler(IInventoryDashboardStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<InventoryDashboardRow>> HandleAsync(
        GetAllInventoryDashboard query, CancellationToken ct)
        => _store.GetAllAsync(ct);
}
