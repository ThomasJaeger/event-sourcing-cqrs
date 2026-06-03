using System.Security.Claims;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// Pattern from Chapter 13: the live-dashboard push hub. A page joins a per-resource group
// (order:{orderId}, inventory:{sku}) and receives a small notification when that resource's read model
// commits a change; the page then re-queries authoritative state (ADR 0027 D1). The hub holds group
// membership only and buffers nothing: a reconnecting page re-queries through its normal load path, so
// there is no replay to serve (ADR 0027 D3).
//
// Subscription is authorized (P9.6). The hub route requires an authenticated connection, and a
// subscribe additionally checks that the caller may read the resource it wants notifications for: the
// check runs at the Api host, which holds the authoritative roles and the ownership rule, and the hub
// joins the group only on an allowed decision. A reader cannot subscribe to a change it could not read.
internal sealed class DashboardHub : Hub
{
    private readonly ISubscriptionAuthorizationClient _authorizationClient;

    // Connection-scoped key under which a successful authorized subscribe caches the tenant, so
    // unsubscribe can rebuild the same qualified group without re-authorizing or reading a claim.
    private const string TenantItemKey = "tenant";

    public DashboardHub(ISubscriptionAuthorizationClient authorizationClient)
    {
        ArgumentNullException.ThrowIfNull(authorizationClient);
        _authorizationClient = authorizationClient;
    }

    // The page joins a resource's group to receive its notifications. The group name is the per-resource
    // string the backplane dispatch broadcasts to; the page builds it from the resource it displays. The
    // subscribe is authorized before the connection joins the group, so an unauthorized attempt throws
    // and never reaches AddToGroupAsync.
    public async Task SubscribeToResource(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        // The client supplies only the resource (prefix and id). The hub joins the tenant-qualified group
        // built from the authorized tenant, so the client never controls the tenant segment.
        var (qualifiedGroup, tenant) = await AuthorizeSubscriptionAsync(group);
        await Groups.AddToGroupAsync(Context.ConnectionId, qualifiedGroup);
        // Cache the authorized tenant on the connection, only after a successful authorized join, so
        // UnsubscribeFromResource rebuilds the same qualified group without an authorize call.
        Context.Items[TenantItemKey] = tenant;
    }

    // Leaving a group needs no authorization: a client may always drop its own subscription, and a
    // membership it never held is a no-op. Gating it would let an unauthorized caller learn nothing it
    // does not already know. The group the connection actually joined is tenant-qualified, so unsubscribe
    // rebuilds it from the same parse and the tenant cached at subscribe, never an authorize call or a
    // claim. A connection that never completed an authorized subscribe cached no tenant, so there is
    // nothing to leave.
    public Task UnsubscribeFromResource(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        var (prefix, resourceId, _) = ParseResource(group);
        if (Context.Items.TryGetValue(TenantItemKey, out var cached) && cached is TenantId tenant)
        {
            var qualifiedGroup = HubGroup.ForResource(tenant, prefix, resourceId);
            return Groups.RemoveFromGroupAsync(Context.ConnectionId, qualifiedGroup);
        }
        return Task.CompletedTask;
    }

    // Resolves the caller's actor id from the connection's authenticated principal, maps the group to a
    // typed resource, and asks the Api host whether the actor may subscribe. Throws
    // UnauthorizedSubscriptionException on every refusal, with the same message for every reason so the
    // client cannot tell a resource it may not see from one that does not exist. The prefix and id are
    // parsed here, before the network call, so a malformed group or an unknown prefix fails closed at the
    // hub with no hop to the Api host. Returns the tenant-qualified group the caller joins, built from
    // the tenant the Api host authorizes, never from the client group string.
    private async Task<(string Group, TenantId Tenant)> AuthorizeSubscriptionAsync(string group)
    {
        var actorClaim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(actorClaim, out var actorId))
        {
            throw new UnauthorizedSubscriptionException();
        }

        var (prefix, resourceId, resourceType) = ParseResource(group);

        var result = await _authorizationClient.AuthorizeAsync(
            actorId,
            new SubscriptionAuthorizationRequest(resourceType, resourceId),
            Context.ConnectionAborted);
        if (!result.Allowed)
        {
            throw new UnauthorizedSubscriptionException();
        }

        // An allow must carry the authoritative tenant. An allow with no tenant is a contract violation,
        // so fail closed rather than join an unqualified or defaulted group, which would cross-tenant
        // leak. The tenant is sourced only from the authorize response, never from the client group.
        if (result.Tenant is null)
        {
            throw new UnauthorizedSubscriptionException();
        }
        var tenant = TenantId.From(result.Tenant.Value);
        return (HubGroup.ForResource(tenant, prefix, resourceId), tenant);
    }

    // Parses a client group string ("{prefix}:{resourceId}") into its parts, shared by subscribe and
    // unsubscribe so both treat malformed input and an unknown prefix identically (throwing
    // UnauthorizedSubscriptionException). The order id must be a Guid; the inventory id is a free-form
    // sku, so it is not parsed here.
    private static (string Prefix, string ResourceId, SubscriptionResourceType ResourceType) ParseResource(string group)
    {
        var separator = group.IndexOf(':');
        if (separator <= 0 || separator == group.Length - 1)
        {
            throw new UnauthorizedSubscriptionException();
        }
        var prefix = group[..separator];
        var resourceId = group[(separator + 1)..];

        var resourceType = prefix switch
        {
            "order" => SubscriptionResourceType.Order,
            "inventory" => SubscriptionResourceType.Inventory,
            // Any other prefix fails closed at the hub: its consumers do not exist in v1, so there is
            // nothing to authorize and no reason to reach the Api host.
            _ => throw new UnauthorizedSubscriptionException(),
        };

        if (resourceType == SubscriptionResourceType.Order && !Guid.TryParse(resourceId, out _))
        {
            throw new UnauthorizedSubscriptionException();
        }

        return (prefix, resourceId, resourceType);
    }
}
