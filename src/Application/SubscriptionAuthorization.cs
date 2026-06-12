using System.Text.Json.Serialization;

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
    CustomerOrders,
}

// The actor is not on the request: the Api host reads it from the authenticated principal (the signed
// forwarded identity), never from the body, so a caller cannot ask whether someone else may subscribe.
public sealed record SubscriptionAuthorizationRequest(SubscriptionResourceType ResourceType, string ResourceId);

// Carries the decision and, on allow, the authoritative tenant the caller is scoped to, so the Web hub
// can build a tenant-qualified group from a trusted source rather than a wire claim. The tenant is
// omitted from the JSON when null (deny), so a denied response stays byte-identical whether the resource
// is not owned or does not exist: the existence-hiding invariant the order read enforces (ADR 0028).
public sealed record SubscriptionAuthorizationResponse(
    bool Allowed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? Tenant = null);

// The Web hub's outcome of an authorize call: the decision plus, on allow, the authoritative tenant the
// caller is scoped to. The hub builds a tenant-qualified SignalR group from this tenant, sourced from
// the Api host's response rather than any wire claim. Tenant is null on deny, and stays null until the
// response carries it, so the deny path is unchanged.
public sealed record SubscriptionAuthorizationResult(bool Allowed, Guid? Tenant);
