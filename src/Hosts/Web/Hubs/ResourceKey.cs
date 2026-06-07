using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// The in-process routing key, the single key-derivation site that replaces the formatted SignalR group
// string (HubGroup.ForResource) and its re-parse. A subscriber registers under a key and the dispatcher
// derives the same key from a published envelope, so the publish side and the subscribe side cannot drift:
// the key is a typed value compared by value, not a string composed on one side and parsed on the other.
//
// The tenant is part of the key. The same resource id under two tenants is two distinct keys, so one
// tenant's notification never reaches the other tenant's subscriber. That makes cross-tenant isolation a
// structural property of the routing rather than a predicate a reviewer must remember to add. ADR 0031,
// ADR 0032 (superseding the tenant-qualified group string of ADR 0027 P10.7).
internal sealed record ResourceKey(TenantId Tenant, SubscriptionResourceType ResourceType, string ResourceId);
