using Microsoft.AspNetCore.SignalR;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// Thrown at the hub when a principal may not subscribe to a resource group, whether because its roles
// lack the required permission or because it does not own the resource. The message is uniform across
// not-owned and not-found so a caller cannot tell a resource it may not see from one that does not
// exist. Derives from HubException because that is the exception type SignalR relays to the calling
// client; the failure is owned at this boundary.
public sealed class UnauthorizedSubscriptionException : HubException
{
    public UnauthorizedSubscriptionException()
        : base("The subscription was refused.")
    {
    }
}
