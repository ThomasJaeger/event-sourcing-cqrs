using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;

namespace EventSourcingCqrs.Application.Queries.Sales;

// Returns a paged slice of the order-list read model, newest-first by PlacedUtc.
// The handler reads the page from IOrderListStore.GetPageAsync and, for an
// owner-scoped principal (a customer), filters it to the orders that principal
// owns; the store clamps oversized limits internally so the surface stays a thin
// pass-through for the operational case. Requires ViewOrder, which Customer,
// Support, and Admin hold.
public sealed record ListOrders(int Offset, int Limit)
    : IQuery<IReadOnlyList<OrderListRow>>, IAuthorizedQuery
{
    public static Permission RequiredPermission => Permission.ViewOrder;
}

public sealed class ListOrdersHandler : IQueryHandler<ListOrders, IReadOnlyList<OrderListRow>>
{
    private readonly IOrderListStore _store;
    private readonly IQueryContextAccessor _context;
    private readonly IPermissionAuthorizer _authorizer;
    private readonly IResourceOwnershipResolver _ownership;

    public ListOrdersHandler(
        IOrderListStore store,
        IQueryContextAccessor context,
        IPermissionAuthorizer authorizer,
        IResourceOwnershipResolver ownership)
    {
        _store = store;
        _context = context;
        _authorizer = authorizer;
        _ownership = ownership;
    }

    public async Task<IReadOnlyList<OrderListRow>> HandleAsync(ListOrders query, CancellationToken ct)
    {
        var page = await _store.GetPageAsync(query.Offset, query.Limit, ct);

        var ownerId = OwnerScopedCustomerId();
        if (ownerId is null)
        {
            return page;
        }

        // Filtering after paging can hand a customer a short page: the store pages over all orders and
        // the filter then drops the rows the customer does not own. The foundation accepts this; pushing
        // the ownership predicate into IOrderListStore.GetPageAsync so a customer-scoped query pages in
        // the store is a Phase 10 refinement (the store signature is unchanged here).
        return page.Where(row => row.CustomerId == ownerId.Value).ToList();
    }

    // The customer id to filter by when the asking principal is owner-scoped (a Customer, which does not
    // hold ViewCustomer), or null when the principal is operational (Support, Admin) and sees every
    // order. The probe is permission-based, not a role-name check (ADR 0028). A non-authenticated query
    // (the bare AskAsync path) is treated as operational, the same gate the authorization behavior uses.
    private Guid? OwnerScopedCustomerId()
    {
        var context = _context.Current;
        if (context is not { IsAuthenticatedUserQuery: true }
            || _authorizer.IsAuthorized(context.Roles, Permission.ViewCustomer))
        {
            return null;
        }

        return _ownership.ResolveCustomerId(context.ActorId);
    }
}
