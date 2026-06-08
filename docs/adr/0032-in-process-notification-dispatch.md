# 0032. In-Process Notification Dispatch for Live Dashboard Updates

## Status

Accepted (June 2026). Supersedes ADR 0027.

## Context

ADR 0027 carried projection-commit notifications to server-rendered Blazor dashboards over a SignalR hub with a PostgreSQL LISTEN/NOTIFY backplane. Phase 11 established that the hub has no viable in-version consumer. The dashboards render server-side on the same host that already receives every projection_committed notification in process, so a page can consume notifications directly without a network round trip to a hub.

Three transports were considered and rejected. A server-side loopback HubConnection from the host back to its own hub is circular, and as specified it also failed on the cookie authentication scheme, TLS to self, and a missing TenantId converter. A hybrid that retained the hub as test-only code is runtime-dead code, and it leaves the ADR-0031 subscription-coverage mandate asserting against a path no request reaches. A browser JavaScript or WASM SignalR client is the path an out-of-process consumer would take, but no such consumer exists in this version, and born-at-consumer holds that the transport is not built ahead of one.

## Decision

Consume projection-commit notifications in process. A single backplane reader holds the one PostgreSQL LISTEN and feeds an in-process dispatcher, which fans each notification out to circuit-scoped subscribers keyed on a typed ResourceKey carrying the tenant, the resource type, and the resource id. Each Blazor circuit registers a subscription through the existing subscription-authorization gate, is keyed on the tenant the authorization allow carries rather than any caller-supplied value, and re-queries authoritative state on receipt, marshalled onto the render thread.

The runtime hub, its route, its per-resource group keying, and its subscription-authorization exception are removed. The PostgreSQL LISTEN/NOTIFY backplane, the subscription-authorization client, and the Api authorization endpoint are retained unchanged. Only the in-host transport from the backplane to the page changes.

## Consequences

The notification path no longer crosses a network boundary or a serialization step inside the host, so the per-resource group string and its parse-format drift risk are gone, replaced by the typed key's ordinal equality. Per-subscriber bounded coalescing in the dispatcher takes the place of the hub-side rate limiting Phase 11 planned. The notification remains best-effort with no replay, as ADR 0027 D1 and D3 set out, so a page that needs settled state when no notification arrives keeps a bounded re-query or a degraded signal rather than assuming delivery. An out-of-process consumer, if one is ever built, reintroduces a client transport at that point, against this same backplane and authorization gate.

## Relationship to other ADRs

Supersedes ADR 0027 (SignalR Hub Topology with PostgreSQL LISTEN/NOTIFY Carrier), now marked superseded. Re-points the ADR-0031 subscription-coverage mandate onto the in-process dispatcher: the cross-tenant routing isolation and the subscribe-key tenant sourcing are proven against the dispatcher under the same enum-exhaustiveness gate. The Chapter 13 and Part 4 manuscript reconciliation of this transport change is deferred to the Phase 17 manuscript arc and tracked as an F-0012-family cross-track flag, with the exact flag id pinned against the book repo index.
