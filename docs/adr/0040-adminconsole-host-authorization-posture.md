# 0040. AdminConsole Host Authorization Posture

## Status

Accepted (June 2026). Establishes the security trust boundary and deny-by-default
authorization posture for the AdminConsole host. Extends ADR 0038's Revisit-when: the
Web-host posture is superseded for pages behind this gate.

## Context

Phase 12 opens the AdminConsole host with the Projection Status Dashboard, the first page
on that host to expose data (per-projection checkpoint and lag, ADR 0039). The host today
is a bare Blazor Server shell with no authentication and no authorization wiring.

ADR 0038 set the Web host's admin-page posture: no host route gate and no page gate, with
the Api host as the trust boundary that gates the query and the subscribe path at dispatch,
and the page rendering a denied state on ApiAuthorizationException. That posture holds for
the Web host because its admin data path is an HTTP call into the Api host, where
AuthorizationQueryBehavior enforces the required permission.

The AdminConsole data path differs. The Projection Status Dashboard wires ProjectionLagReader
into DI and reads PostgreSQL in process, following the Workers-host composition pattern. No
Api-host dispatch sits in that path, so there is no downstream boundary to enforce
authorization. The AdminConsole must be its own trust boundary.

The permission model (ADR 0028) is permission-based: a guard asks whether the principal holds
the permission an operation requires, derived from the principal's roles through
IPermissionAuthorizer against the static role-to-permission policy. Enforcement today lives
only at command and query dispatch in Application. Permissions are never materialized as
claims. The Web host's operator cookie principal carries one ClaimTypes.NameIdentifier claim
and no role or permission claim; roles are resolved server-side from the current-roles read
model, keyed on the authenticated actor id. No permission claim exists for an ASP.NET
RequireClaim policy to assert against.

## Decision

The AdminConsole host fails closed. A host-level fallback authorization policy gates every
endpoint that carries no other authorization metadata. A page is gated by default; only an
explicit AllowAnonymous opts out, which the login surface uses. The Projection Status
Dashboard sits behind the fallback with no per-page attribute.

The fallback policy is realized as a custom IAuthorizationRequirement and its
AuthorizationHandler. The handler reads the principal's NameIdentifier, resolves the actor's
roles server-side from the current-roles read model (the authoritative keying the Api host
uses through CurrentRolesPrincipalFactory), and calls the existing IPermissionAuthorizer
against the required permission. This reuses the single authorizer and keeps permissions
un-serialized, consistent with ADR 0028.

The fallback requires a new permission, Permission.AccessAdminConsole, minted at this consumer
per ADR 0028's prescription that the enumeration extends as required permissions are
reconciled. AccessAdminConsole is a console-access capability granted to the Admin role in the
static role-to-permission policy. It guards host access; per-page permission grain is deferred
to the consumer that first needs an operator to hold one AdminConsole page without another,
layered as a page policy over the fallback.

The AdminConsole gains cookie authentication and a sign-in path that establishes the operator
cookie principal, mirroring the Web host's name-identifier-only shape. This is enough
authentication substrate to prove the gate through a route or integration test, which ADR 0038
requires of a declarative route-level gate before it earns its place. The interactive login
page and logout chrome are born at their own consumer.

Rejected mechanisms. Materializing role or permission claims on the cookie principal at login
and gating with RequireClaim was rejected: a claim minted at login goes stale when roles change
mid-session, the failure ADR 0028 avoids by keeping roles authoritative in the read model, and
it builds a parallel claim substrate the system deliberately does without. A bare
RequireAuthenticatedUser fallback was rejected: it admits any authenticated principal, the
bare-authenticated posture an operator console must not take, and it widens silently the moment
a second authentication path appears. A page-level Authorize convention with no host fallback
was rejected: opt-in leaves a new page open until someone adds the attribute, and an operator
surface that depends on remembering to gate each page is the anti-pattern the host fallback
removes. Gating below the endpoint with an authorization decorator on the reader was rejected:
it executes host code and reaches the database before denying, and it is machinery the
single-consumer reader does not need when the route gate denies first.

## Consequences

The AdminConsole is its own in-process trust boundary. Every current and future AdminConsole
page fails closed by default; a page added without authorization metadata is denied rather than
exposed. The Event Store Browser and Correlation-ID Tracer (Phase 12) inherit this posture with
no per-page work. The handler reuses IPermissionAuthorizer, so the role-to-permission policy
stays the one source of authorization truth across hosts. The AdminConsole acquires a
current-roles read dependency for server-side role resolution. ADR 0038's Web-host posture is
superseded for pages behind this gate.

A wording-versus-code divergence in ADR 0028 is noted, not resolved here: its Decision states
that roles and permissions live on the runtime principal, while the code computes permissions
from roles at dispatch and never places them on the principal, and role claims are advisory at
the Api edge. This is why the handler-plus-IPermissionAuthorizer mechanism is design-consistent
and a claim-materializing mechanism is not. The divergence is flagged for a later explicit
ADR-0028 reconciliation.

## Revisit when

An AdminConsole page needs per-operator page-level differentiation (one operator holds one page
without another), at which point a page policy layers over the fallback and the per-page
permission grain is decided at that consumer. Or the operator login flow is built out, at which
point the deferred login page and logout surface land against this record.
