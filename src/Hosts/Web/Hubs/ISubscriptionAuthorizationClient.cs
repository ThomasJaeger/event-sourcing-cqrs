using EventSourcingCqrs.Application;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// The dashboard hub's outbound port to the Api host's subscription-authorization endpoint (P9.6). The
// hub asks whether a given actor may subscribe to a resource; the implementation signs the call with
// the actor's forwarded identity and posts to the Api host. The actor is a parameter, not read from a
// circuit: a hub method runs on its own SignalR connection scope and has no Blazor circuit, so it
// cannot use the circuit-scoped identity provider ApiClient signs through.
public interface ISubscriptionAuthorizationClient
{
    Task<SubscriptionAuthorizationResult> AuthorizeAsync(Guid actorId, SubscriptionAuthorizationRequest request, CancellationToken ct);
}
