using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Projections.OrderList;

namespace EventSourcingCqrs.Application.Queries.Sales;

// Returns a paged slice of the order-list read model, newest-first by
// PlacedUtc. The handler is a one-liner over IOrderListStore.GetPageAsync;
// the store clamps oversized limits internally so this surface stays a
// thin pass-through until Phase 7's API binding adds user-visible
// validation.
public sealed record ListOrders(int Offset, int Limit) : IQuery<IReadOnlyList<OrderListRow>>;

public sealed class ListOrdersHandler : IQueryHandler<ListOrders, IReadOnlyList<OrderListRow>>
{
    private readonly IOrderListStore _store;

    public ListOrdersHandler(IOrderListStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<OrderListRow>> HandleAsync(ListOrders query, CancellationToken ct)
        => _store.GetPageAsync(query.Offset, query.Limit, ct);
}
