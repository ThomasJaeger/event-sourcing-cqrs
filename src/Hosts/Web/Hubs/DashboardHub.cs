using Microsoft.AspNetCore.SignalR;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// Pattern from Chapter 13: the live-dashboard push hub. A page joins a
// per-resource group (order:{orderId}, inventory:{sku}) and receives a small
// notification when that resource's read model commits a change; the page then
// re-queries authoritative state (ADR 0027 D1). The hub holds group membership
// only and buffers nothing: a reconnecting page re-queries through its normal
// load path, so there is no replay to serve (ADR 0027 D3).
internal sealed class DashboardHub : Hub
{
    // The page joins a resource's group to receive its notifications. The group
    // name is the per-resource string the backplane dispatch broadcasts to; the
    // page builds it from the resource it displays.
    public Task SubscribeToResource(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    public Task UnsubscribeFromResource(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }
}
