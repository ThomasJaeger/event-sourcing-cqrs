using System.Diagnostics.CodeAnalysis;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// Maps a publishing projection to the resource type its notifications route to, the in-process successor
// of HubBackplaneHostedService's GroupPrefixes. The map is exposed rather than a private static, so the
// coverage harness enumerates it directly: the round-trip is driven from this single map instead of a
// hand-list that could silently drift from it (closing the P10.9 blind spot the hub-era coverage carried).
// A projection that publishes without a mapping is a defect; TryKeyFor returns false and the dispatcher
// logs and drops rather than misrouting. ADR 0031, ADR 0032.
internal static class ResourceRouting
{
    public static IReadOnlyDictionary<string, SubscriptionResourceType> ProjectionResourceTypes { get; } =
        new Dictionary<string, SubscriptionResourceType>
        {
            ["order-detail"] = SubscriptionResourceType.Order,
            ["inventory-dashboard"] = SubscriptionResourceType.Inventory,
            ["order-list"] = SubscriptionResourceType.CustomerOrders,
        };

    // Derives the routing key from an envelope, or false when the projection has no mapping. The key
    // carries the envelope's tenant, so a notification routes only to subscribers of the same tenant.
    public static bool TryKeyFor(NotificationEnvelope envelope, [NotNullWhen(true)] out ResourceKey? key)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (ProjectionResourceTypes.TryGetValue(envelope.ProjectionName, out var resourceType))
        {
            key = new ResourceKey(envelope.Tenant, resourceType, envelope.ResourceId);
            return true;
        }
        key = null;
        return false;
    }
}
