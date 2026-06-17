# 0038. Web-Host Admin-Page Authorization Posture

## Status

Accepted (June 2026). Records the authorization posture for the first admin-scoped Web-host page. Superseded for gated pages when the AdminConsole declarative gate lands (Phase 12).

## Context

The order-throughput meter at /admin/throughput (P11.12b RED #7, commit e882b23) is the first admin-scoped page in the Web host. The Web host today enforces no page-level and no route-level authorization. AddAuthorization() is registered bare, with no fallback policy and no default policy. No page carries an [Authorize] attribute, AuthorizeRouteView runs with no policy, and the [Authorize] namespace is not imported anywhere in the Razor tree.

Permission enforcement lives one host over, at query dispatch in the Api host. AuthorizationQueryBehavior reads the IAuthorizedQuery contract and refuses a query whose required permission the principal lacks. The Web host reaches that behavior over HTTP through IApiClient. GetOrderThroughput is gated on Permission.ViewOrderThroughput at dispatch, and the subscribe arm is gated on the same permission at SubscriptionsEndpoint. A denial returns across the HTTP boundary, and the Web host receives it as ApiAuthorizationException.

## Decision

The admin page carries no Web-host route gate and no page gate. The trust boundary is the Api host, which gates both the query and the subscribe path on the order-throughput permission and is proven by test. The page handles the denial it receives: on ApiAuthorizationException it renders a denied state and does not arm the subscription.

A declarative Web-host gate is deferred to the AdminConsole host (Phase 12). A route-level authorization convention earns its place when it has a second consumer and can be proven by a route test or an integration test, rather than being introduced unproven inside a bUnit-only slice.

## Consequences

The Web host stays an HTTP client of the Api trust boundary. An authenticated non-admin who reaches the route gets a denied render, not data. No permission-to-policy bridge and no role-claim shape is introduced on the Web principal ahead of the AdminConsole need. The page-level denied-state handling is the pattern admin pages follow until the declarative gate lands.

## Revisit when

The AdminConsole host (Phase 12) introduces the declarative route-level gate. At that point this posture is superseded for pages behind that gate.
