using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// The sole SignalR group-name construction site, shared by the broadcast side
// (HubBackplaneHostedService, building from the envelope tenant) and the subscribe
// side (DashboardHub, building from the authorized tenant). Both must produce a
// byte-identical string or a broadcast reaches no subscriber.
internal static class HubGroup
{
    // The tenant-qualified SignalR group name. The tenant segment is always present:
    // a missing qualifier is a cross-tenant broadcast leak, not a default-tenant
    // group. It renders in the same N-format the stream id uses for its tenant
    // segment (StreamId.Compose), so the same tenant produces the same bytes on both
    // sides. The prefix names the resource family (order, inventory) and the
    // resource id is the per-resource key (an order guid, a sku).
    public static string ForResource(TenantId tenant, string prefix, string resourceId)
        => $"tenant:{tenant.Value:N}:{prefix}:{resourceId}";
}
