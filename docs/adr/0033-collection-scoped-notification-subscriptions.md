# 0033. Collection-Scoped Notification Subscriptions for List Dashboards

## Status

Accepted (June 2026). Extends ADR 0032. Amended (June 2026) by ADR 0037 for collections scoped to one owning principal.

## Context

ADR 0032 established in-process notification dispatch: a projection commit publishes a NotificationEnvelope, the dispatcher fans it to circuit-scoped subscribers keyed on a typed ResourceKey of tenant, resource type, and resource id, and each subscriber re-queries authoritative state on receipt. The single-resource pages retrofitted under 0032, OrderDetail and the OrderCreate wizard, each subscribe under one resource id, the order id, and OrderDetailProjection emits that same id, so the key matches one page to one resource.

The InventoryDashboard is a collection page. It renders every SKU for the tenant under one route with no resource parameter, so it has no single resource id to key on. InventoryDashboardProjection emitted a per-SKU resource id, which the exact-equality dispatcher delivers only to a subscriber keyed on that exact SKU. A collection page cannot enumerate a stable key set ahead of receipt: a per-visible-SKU subscription would register one subscription per row and would still miss any SKU another actor creates after load. Phase 11 established that the per-SKU notification has no live consumer: the page polls, and nothing subscribes to inventory.

## Decision

A tenant-wide collection page subscribes under a stable collection-scoped sentinel resource id, and the publishing projection emits the same sentinel. InventoryDashboardProjection emits CollectionResourceIds.AllInventory for every inventory change, and the InventoryDashboard page subscribes under that one sentinel and re-queries the whole list on any notification. The per-SKU resource id is removed; it had no consumer.

The sentinel is a single shared constant in Domain.Abstractions, CollectionResourceIds.AllInventory, referenced by both the projection emit and the page subscribe so the two sides are byte-identical, since the dispatcher routes by ordinal ResourceKey equality. The value is a non-empty, non-whitespace string: the subscribe side rejects null or whitespace.

The dispatcher is unchanged. Routing stays exact-key equality; the sentinel is an ordinary resource id under that scheme, not a wildcard and not a prefix match. No new SubscriptionResourceType value and no new route: SubscriptionResourceType.Inventory and the inventory-dashboard routing entry already exist.

## Consequences

A collection page receives one notification per relevant change for the tenant and re-queries authoritative state, so it reflects changes made by any actor, including newly created resources, without enumerating a key set. The re-query reads the full list, so a high inventory change rate drives a full-list re-query per change on every open dashboard circuit. This follows 0032's re-query-authoritative-state model and is bounded by the dispatcher's per-subscriber coalescing; tighter coalescing is deferred until a measured load signal calls for it.

The sentinel carries no per-resource ownership, which suits inventory: its subscription authorization is a blanket ViewInventory permission with no per-SKU ownership check, so a tenant-wide key loses no ownership granularity. Cross-tenant isolation now rests entirely on the tenant field of the ResourceKey, since the sentinel is constant across SKUs; that isolation is proven by a cross-tenant routing test under the coverage mandate ADR 0031 sets, not by the resource id varying.

The collection-sentinel pattern is the template the remaining tenant-wide collection dashboards follow. Each adds its own constant to CollectionResourceIds and emits and subscribes under it. A collection scoped to one owning principal follows ADR 0037, which records why a sentinel is the wrong shape for it.

## Relationship to other ADRs

Extends ADR 0032 (In-Process Notification Dispatch for Live Dashboard Updates): the collection sentinel is a resource-id convention layered onto the dispatch model 0032 defines, with the dispatcher and its typed key unchanged. ADR 0031 (Tenant Isolation by a Shared-Schema Discriminator) supplies the cross-tenant coverage mandate the sentinel's tenant-only separation rests on. The Chapter 13 and Part 4 manuscript reconciliation of the collection-subscription convention is deferred to the Phase 17 manuscript arc and tracked in the F-0012-family cross-track flag, with the exact flag id pinned against the book repo index.
