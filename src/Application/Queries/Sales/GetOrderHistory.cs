using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Queries.Sales;

public sealed record GetOrderHistory(Guid OrderId) : IQuery<OrderHistoryView?>, IAuthorizedQuery
{
    public static Permission RequiredPermission => Permission.ViewOrder;
}

public sealed class GetOrderHistoryHandler : IQueryHandler<GetOrderHistory, OrderHistoryView?>
{
    private readonly OrderHistoryReader _reader;
    private readonly IQueryContextAccessor _context;
    private readonly ICurrentTenantAccessor _tenants;
    private readonly IPermissionAuthorizer _authorizer;
    private readonly IResourceOwnershipResolver _ownership;

    public GetOrderHistoryHandler(OrderHistoryReader reader, IQueryContextAccessor context,
        ICurrentTenantAccessor tenants, IPermissionAuthorizer authorizer, IResourceOwnershipResolver ownership)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(ownership);
        _reader = reader;
        _context = context;
        _tenants = tenants;
        _authorizer = authorizer;
        _ownership = ownership;
    }

    public Task<OrderHistoryView?> HandleAsync(GetOrderHistory query, CancellationToken ct)
    {
        var tenant = _tenants.Current ?? throw new MissingTenantContextException();
        var context = _context.Current;
        Guid? owner = context is { IsAuthenticatedUserQuery: true }
            && !_authorizer.IsAuthorized(context.Roles, Permission.ViewCustomer)
                ? _ownership.ResolveCustomerId(context.ActorId)
                : null;
        return _reader.ReadAsync(tenant, query.OrderId, owner, ct);
    }
}
