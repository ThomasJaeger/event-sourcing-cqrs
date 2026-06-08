namespace EventSourcingCqrs.Domain.Abstractions;

// Resource-id sentinels for collection-scoped notifications. A collection page,
// one whose live view spans every resource of a type for a tenant rather than a
// single resource, subscribes under a stable sentinel resource-id, and the
// publishing projection emits the same sentinel. The dispatcher routes by exact
// ResourceKey equality, so the value is byte-identical on both sides and
// non-empty: the subscribe side rejects null or whitespace.
public static class CollectionResourceIds
{
    // Every inventory resource for the tenant. The InventoryDashboard is a
    // collection page over all SKUs, so the projection emits and the page
    // subscribes under this sentinel rather than a per-sku resource-id.
    public const string AllInventory = "all-inventory";
}
