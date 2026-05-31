using System.Security.Claims;
using EventSourcingCqrs.Application;
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

        await AuthorizeSubscriptionAsync(group);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    // Leaving a group needs no authorization: a client may always drop its own subscription, and a
    // membership it never held is a no-op. Gating it would let an unauthorized caller learn nothing it
    // does not already know.
    public Task UnsubscribeFromResource(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }

    // Resolves the caller's actor id from the connection's authenticated principal, maps the group to a
    // typed resource, and asks the Api host whether the actor may subscribe. Throws
    // UnauthorizedSubscriptionException on every refusal, with the same message for every reason so the
    // client cannot tell a resource it may not see from one that does not exist. The prefix and id are
    // parsed here, before the network call, so a malformed group or an unknown prefix fails closed at the
    // hub with no hop to the Api host.
    private async Task AuthorizeSubscriptionAsync(string group)
    {
        var actorClaim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(actorClaim, out var actorId))
        {
            throw new UnauthorizedSubscriptionException();
        }

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

        // An order group's id must be a Guid; a malformed id is refused at the hub, not posted to the Api
        // host. The inventory id is a free-form sku, so it is not parsed here.
        if (resourceType == SubscriptionResourceType.Order && !Guid.TryParse(resourceId, out _))
        {
            throw new UnauthorizedSubscriptionException();
        }

        var allowed = await _authorizationClient.AuthorizeAsync(
            actorId,
            new SubscriptionAuthorizationRequest(resourceType, resourceId),
            Context.ConnectionAborted);
        if (!allowed)
        {
            throw new UnauthorizedSubscriptionException();
        }
    }
}
