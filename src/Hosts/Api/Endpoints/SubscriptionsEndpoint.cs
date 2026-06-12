using System.Security.Claims;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;

namespace EventSourcingCqrs.Hosts.Api.Endpoints;

// POST /subscriptions/authorize: answers whether the authenticated actor may subscribe to a dashboard
// resource's change notifications (P9.6). The Web host's hub calls this before it joins a SignalR
// group; the decision reuses the same permission gate and ownership rule a read of the resource runs
// under, so a subscription can never see a change the caller could not read (ADR 0027, ADR 0028).
//
// The route requires authentication, so the actor is established and carries its id as the name
// identifier; the endpoint loads the actor's authoritative roles (never the forwarded claim) the same
// way /queries does. The response carries only a boolean: a denied decision is Allowed=false, not a
// 403, and a not-owned order is the same Allowed=false as a not-found one, so the response leaks
// neither the reason nor the resource's existence.
public static class SubscriptionsEndpoint
{
    public static async Task<IResult> AuthorizeAsync(
        SubscriptionAuthorizationRequest request,
        ClaimsPrincipal user,
        IPrincipalFactory principalFactory,
        IPermissionAuthorizer authorizer,
        IResourceOwnershipResolver ownership,
        IOrderDetailStore orderDetailStore,
        ICurrentTenantAccessor tenantAccessor,
        CancellationToken ct)
    {
        var actorClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(actorClaim, out var actorId))
        {
            return Results.Unauthorized();
        }
        var principal = await principalFactory.CreateAsync(actorId, ct);

        // The ownership check reads the order-detail header outside the query bus, so this path
        // sets the current tenant from the principal and restores it after, the same set-and-restore
        // the query bus runs. Without it the header read fails closed on an unset tenant.
        var previousTenant = tenantAccessor.Current;
        tenantAccessor.Current = principal.Tenant;
        try
        {
            var allowed = request.ResourceType switch
            {
                SubscriptionResourceType.Order => await IsOrderSubscriptionAllowedAsync(
                    request.ResourceId, actorId, principal.Roles, authorizer, ownership, orderDetailStore, ct),
                SubscriptionResourceType.Inventory =>
                    authorizer.IsAuthorized(principal.Roles, Permission.ViewInventory),
                SubscriptionResourceType.CustomerOrders => IsCustomerOrdersSubscriptionAllowed(
                    request.ResourceId, actorId, principal.Roles, authorizer, ownership),
                _ => false,
            };

            // The tenant rides the response only on allow (null is omitted from the JSON), so the deny
            // bytes are unchanged and the Web hub builds its group from the authoritative tenant.
            return Results.Ok(new SubscriptionAuthorizationResponse(allowed, allowed ? principal.Tenant.Value : null));
        }
        finally
        {
            tenantAccessor.Current = previousTenant;
        }
    }

    // Reproduces GetOrderDetailHandler's gate-and-ownership split: the ViewOrder gate, then the
    // operational-versus-owner branch keyed on ViewCustomer. An operational principal (Support, Admin)
    // may subscribe to any order; an owner-scoped principal (Customer) may subscribe only to an order
    // whose read-model header records it as the owning customer. A header that does not exist denies
    // identically to a not-owned one, so the decision hides the order's existence.
    private static async Task<bool> IsOrderSubscriptionAllowedAsync(
        string resourceId,
        Guid actorId,
        IReadOnlyCollection<Role> roles,
        IPermissionAuthorizer authorizer,
        IResourceOwnershipResolver ownership,
        IOrderDetailStore orderDetailStore,
        CancellationToken ct)
    {
        if (!authorizer.IsAuthorized(roles, Permission.ViewOrder))
        {
            return false;
        }

        // Operational principals see any order; only an owner-scoped principal is filtered to its own.
        if (authorizer.IsAuthorized(roles, Permission.ViewCustomer))
        {
            return true;
        }

        // A malformed order id is a denied decision, not a 500: a bad group string cannot probe.
        if (!Guid.TryParse(resourceId, out var orderId))
        {
            return false;
        }

        var ownerId = ownership.ResolveCustomerId(actorId);
        var header = await orderDetailStore.GetHeaderAsync(orderId, ct);
        return header is not null && header.CustomerId == ownerId;
    }

    // The same gate-and-ownership split for the customer-orders collection, decided without a read-model
    // read: ownership is the actor-is-customer convention (IResourceOwnershipResolver maps the actor to
    // the customer id it owns), so the decision compares ids and needs no tenant scope. An operational
    // principal (Support, Admin) may subscribe to any customer's collection; an owner-scoped principal
    // (Customer) only to its own. A malformed customer id is a denied decision, not a 500.
    private static bool IsCustomerOrdersSubscriptionAllowed(
        string resourceId,
        Guid actorId,
        IReadOnlyCollection<Role> roles,
        IPermissionAuthorizer authorizer,
        IResourceOwnershipResolver ownership)
    {
        if (!authorizer.IsAuthorized(roles, Permission.ViewOrder))
        {
            return false;
        }

        if (authorizer.IsAuthorized(roles, Permission.ViewCustomer))
        {
            return true;
        }

        if (!Guid.TryParse(resourceId, out var customerId))
        {
            return false;
        }

        return ownership.ResolveCustomerId(actorId) == customerId;
    }
}
