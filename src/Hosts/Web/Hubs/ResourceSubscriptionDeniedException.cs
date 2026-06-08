namespace EventSourcingCqrs.Hosts.Web.Hubs;

// Thrown when a circuit-scoped subscription is refused: the Api gate denied it, or it returned an allow
// without the authoritative tenant the subscription must key on. A Blazor page subscription has no SignalR
// client to relay a refusal to, so it fails closed with a plain exception its caller owns. The message is
// uniform and reasonless, so a denial leaks nothing about why. ADR 0032.
internal sealed class ResourceSubscriptionDeniedException : Exception
{
    public ResourceSubscriptionDeniedException()
        : base("The subscription was denied.")
    {
    }
}
