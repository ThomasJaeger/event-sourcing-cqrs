using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;

namespace EventSourcingCqrs.Application.Queries.Sales;

// The composed order-detail view: the header row, its line items, and the JSONB
// event timeline. OrderDetail is the first query that composes multiple read-model
// rows into one view; the view shape lives next to the query that returns it, the
// same one-file-per-pair convention the single-row queries follow. Future
// composed-view queries follow this placement.
//
// The timeline entries are the envelope-rich rows verbatim (ADR 0018, D1): a caller
// reads them by event_type and occurred_utc and deserialises a payload only when
// display detail needs it. The handler does no deserialisation on the read path, so
// a list view that shows only event type and time pays no deserialisation cost.
public sealed record OrderDetailView(
    OrderDetailRow Header,
    IReadOnlyList<OrderDetailLineRow> Lines,
    IReadOnlyList<OrderDetailTimelineRow> Timeline);

// Returns the composed view for an order, or null when no order_detail header exists
// (the order was never observed) or when an owner-scoped principal asks for an order
// it does not own. AddApplication's assembly scan registers the handler, so it needs
// no explicit DI line. Requires ViewOrder, which Customer, Support, and Admin hold.
public sealed record GetOrderDetail(Guid OrderId)
    : IQuery<OrderDetailView?>, IAuthorizedQuery
{
    public static Permission RequiredPermission => Permission.ViewOrder;
}

public sealed class GetOrderDetailHandler : IQueryHandler<GetOrderDetail, OrderDetailView?>
{
    private readonly IOrderDetailStore _store;
    private readonly IQueryContextAccessor _context;
    private readonly IPermissionAuthorizer _authorizer;
    private readonly IResourceOwnershipResolver _ownership;

    public GetOrderDetailHandler(
        IOrderDetailStore store,
        IQueryContextAccessor context,
        IPermissionAuthorizer authorizer,
        IResourceOwnershipResolver ownership)
    {
        _store = store;
        _context = context;
        _authorizer = authorizer;
        _ownership = ownership;
    }

    public async Task<OrderDetailView?> HandleAsync(GetOrderDetail query, CancellationToken ct)
    {
        // No header means the order was never observed; the lines and timeline reads
        // would return empty anyway, so short-circuit to null.
        var header = await _store.GetHeaderAsync(query.OrderId, ct);
        if (header is null)
        {
            return null;
        }

        // An owner-scoped principal (a Customer) sees only an order it owns; for any other order, return
        // null so the endpoint's null-to-404 mapping hides the existence of another customer's order (no
        // existence leak). Operational principals (Support, Admin) see any order.
        var ownerId = OwnerScopedCustomerId();
        if (ownerId is not null && header.CustomerId != ownerId.Value)
        {
            return null;
        }

        var lines = await _store.GetLinesAsync(query.OrderId, ct);
        var timeline = await _store.GetTimelineAsync(query.OrderId, ct);
        return new OrderDetailView(header, lines, timeline);
    }

    // The customer id to filter by when the asking principal is owner-scoped (a Customer, which does not
    // hold ViewCustomer), or null when the principal is operational (Support, Admin). The probe is
    // permission-based, not a role-name check (ADR 0028). A non-authenticated query (the bare AskAsync
    // path) is treated as operational, the same gate the authorization behavior uses.
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
