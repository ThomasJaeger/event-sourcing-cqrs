namespace EventSourcingCqrs.Application;

// The Web-to-Api contract for a dashboard subscription-authorization check (P9.6). The Web host's
// dashboard hub asks the Api host whether the calling actor may subscribe to a resource's change
// notifications; the Api host answers with the same permission gate and ownership rule a read of that
// resource runs under (ADR 0027, ADR 0028). Single-sourced here, alongside CommandAcceptedResponse and
// the other host-to-host transport DTOs, so the Api endpoint and the Web client name one type rather
// than a per-host duplicate.

// The resource kinds a dashboard page subscribes to. Resource-typed rather than the raw SignalR group
// string, so the group syntax (order:{orderId}, inventory:{sku}) stays a Web-host transport detail and
// the Api host authorizes against a typed resource.
public enum SubscriptionResourceType
{
    Order,
    Inventory,
}

// The actor is not on the request: the Api host reads it from the authenticated principal (the signed
// forwarded identity), never from the body, so a caller cannot ask whether someone else may subscribe.
public sealed record SubscriptionAuthorizationRequest(SubscriptionResourceType ResourceType, string ResourceId);

// Carries only the decision. No reason and no resource detail, so a denied response is byte-identical
// whether the resource is not owned or does not exist: the existence-hiding invariant the order read
// enforces (ADR 0028).
public sealed record SubscriptionAuthorizationResponse(bool Allowed);
