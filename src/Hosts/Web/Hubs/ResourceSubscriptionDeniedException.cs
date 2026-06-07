namespace EventSourcingCqrs.Hosts.Web.Hubs;

// Thrown when a circuit-scoped subscription is refused: the Api gate denied it, or it returned an allow
// without the authoritative tenant the subscription must key on. Distinct from the hub's
// UnauthorizedSubscriptionException, which derives from HubException to relay a refusal to a SignalR client;
// a Blazor page subscription has no hub client to relay to, so it fails closed with a plain exception its
// caller owns. The message is uniform and reasonless, so a denial leaks nothing about why. ADR 0032.
internal sealed class ResourceSubscriptionDeniedException : Exception
{
    public ResourceSubscriptionDeniedException()
        : base("The subscription was denied.")
    {
    }
}
