# 0028. Permission-Based Authorization Model

## Status

Accepted (May 2026)

## Context

PLAN.md's v1 scope is amended to ship role-based access control and multi-tenancy. The authentication-and-authorization phase lands first, and its first commit is the policy substrate, before any enforcement wiring. The pre-flight against disk confirmed the surface: no access-control symbols exist (the only role and authorization tokens on disk are the Payment aggregate's unrelated authorize lifecycle), identity is greenfield with the actor in event metadata hardwired to an empty value, and the command pipeline folds logging, idempotency, and validation with no authorization behavior.

The model question is how an authorization decision is expressed and where the policy lives. Two shapes were weighed. Role-name checks inline at each guard ask whether the principal holds a named role, which scatters policy across the codebase and couples every guard to the role taxonomy, so a role rename or a permission regrant touches every call site. Permission-based checks ask whether the principal holds the permission an operation requires, which centralizes the policy in one validated definition and lets every guard ask a single question.

## Decision

Authorization is permission-based. A fixed enumeration of named permissions and a fixed enumeration of roles, with a static role-to-permission policy mapping each role to its permission set.

Permissions and roles are C# enumerations rather than the const-string or static-readonly-record token sets the repository uses elsewhere for closed sets. Those tokens are composed into strings at runtime, born at a string-building consumer; permissions and roles are referenced as discrete compile-time tokens at the command declarations the command-authz commit adds and at the system role's permission set, where an enumeration gives the compile-time safety a string token cannot. The divergence from the const-token precedent is justified by the consumer set, not a departure from it.

The role-to-permission policy is part of the application definition, expressed in code rather than external configuration. One explicit source, no per-host duplication, and no silent default. The policy changes with deployments the way the system-actor identities do.

The policy validates at composition, not at first resolution. The registry validates its policy in the constructor (every role maps to a set; every granted permission is a defined enumeration member), and the Application composition root constructs the registry, so an incomplete or malformed policy throws at host startup. This follows the repository's constructor-guard validation idiom and the production rule that configuration validates at startup with no downstream surprises. It diverges deliberately from the type registries' lazy-at-first-resolution walk, because a policy gap that surfaced lazily would let a misconfigured build look healthy until the first authorized command.

The storage split: the role-to-permission policy stays in the static application definition; user-to-role assignments live in an event-sourced Access surface that the next commit lands, so authorization changes are auditable through the same event store as everything else. Roles and permissions live on the runtime principal and never in event metadata; metadata records who acted, which carries the audit story.

The permission-check port is named `IPermissionAuthorizer` rather than `IAuthorizationService`, because the latter shares its simple name with the ASP.NET Core authorization port the hosts bring into scope when the hub authentication commit lands. Naming the port for the permission decision it makes keeps the host-side call sites unambiguous without a using-alias. The permission check lives in Application. Its consumer set is transport-side and Application-side (the command-authz behavior, the query handlers, and via a host the hub), with no Infrastructure consumer, so Application placement is correct under the consumer-set-shape rule ADR 0026 records. The role-permission registry is a concrete sealed class injected directly, mirroring the query-type-registry placement and registration shape.

The role set is Customer, Support, Admin, and System. The System role holds the permission set process managers exercise; the async-propagation commit wires the system actor to it, and it is defined now so the policy validates as a complete set. Admin holds every permission by definition, computed from the enumeration so the invariant cannot drift.

## Consequences

- The substrate ships in `src/Application/Authorization`: the permission and role enumerations, the static policy, the validating registry, the authorization-service port and its implementation, and the named policy exception. No enforcement wiring. The command-authz behavior, the query and read authz, the hub authentication and ownership check, and the host identity establishment are later commits in this phase.
- The composition root constructs and validates the registry, so a malformed policy is a startup failure with a named exception rather than a first-request failure.
- The permission set is curated at resource-and-operation granularity, not one permission per command. The command-authz commit reconciles each command to a required permission and extends the enumeration as it does.
- This ADR opens the authorization-model record. It is extended through the phase: the Access surface and its assignment storage, the command-authz behavior and its fold position, the query and read authz, the hub authentication and ownership check, and the caused-command authorization under the system actor.
- A future external identity provider supplies the principal. The model consumes roles off the principal regardless of source, so external identity-provider integration stays out of scope here without reshaping the model.

## Amendment: the Access surface, and Role in Domain.Abstractions

The Access surface that records role assignments lands as `UserRoles`, an event-sourced aggregate keyed by user id, with `RoleAssigned` and `RoleRevoked` events and a `CurrentRolesProjection` into a `current_user_roles` read model the principal factory reads. A bootstrap-administrator seed in the Workers host grants the configured administrator the Admin role on startup through an idempotent append, so the system has an assigner before any other assignment is possible.

This forced one change to the placement the original decision recorded. The substrate put `Role` in `Application/Authorization`. The Access events serialize `Role` and the read-model port returns it, so an Application-resident `Role` would force the Postgres read-model adapter and the projection to reference Application, inverting the hexagonal layering rule ADR 0026 records (Infrastructure must not depend on Application). `Role` therefore moves to `Domain.Abstractions`, alongside the other types that cross the persistence boundary (`SystemActor`, the event and stream contracts), and the whole Access surface lives in `Domain/Access` like every other event-sourced context. `Permission` stays in `Application/Authorization`: it is runtime authorization policy, never serialized, so it carries no cross-layer constraint. The policy, the registry, and the authorizer reference `Role` from its new home.

The current-roles unit of work carries no notification-publish member, unlike the six dashboard-feeding units of work: the read model feeds the principal factory, not a live dashboard, so it has no SignalR subscriber and stages nothing (born-at-consumer).

`RoleAssigned` and `RoleRevoked` register through an `AccessEventTypeProvider` in the Workers host, which runs the projection and the seed. The Api host registers no Access provider, consistent with its rule of registering event providers only for the aggregates its command handlers dispatch. User-facing assignment commands are not part of this commit: the bootstrap seed is the only writer, and command-driven assignment lands with the command-authz work or later.

## Extension: Api-side identity establishment (P9.3a)

P9.3 establishes a real principal where the actor was hardwired empty. It is split deliberately. P9.3a is the Api half: a request carries a forwarded identity, the Api host authenticates it, and the actor and its authoritative roles land on the command context and through it on event metadata. P9.3b is the browser half: the Blazor circuit authenticates the user, the Web host forwards the identity to the Api host, and it adds the signature that makes the forwarded header trustworthy across the wire. P9.3a proves that an authenticated request stamps the real actor and an unauthenticated request is rejected; it does not yet prove a browser-to-actor flow.

The scheme is a custom authentication scheme that reads a single combined header carrying the actor id and the upstream's role claim. The handler establishes a `ClaimsPrincipal` from it; the parsing sits behind `IForwardedIdentityReader`. No new package: the scheme is a custom `AuthenticationHandler` over the framework's authentication primitives.

Staged validation and the trust posture: in P9.3a the forwarded header is unsigned and only dev-validated, parsed rather than verified. The Api host must not be exposed to untrusted callers carrying this header until P9.3b adds shared-secret signature validation at the `IForwardedIdentityReader` seam. Until then the header is a same-trust-boundary mechanism between the Web host and the Api host, not an authentication credential a hostile client could not forge.

Authoritative roles, not the forwarded claim: the command context's roles come from the current-roles read model, loaded by the principal factory keyed on the authenticated actor id, not from the header's role claim. The worst an unsigned header can assert is an actor id; the roles that actor holds are what the system recorded. Loading roles rather than trusting the claim is why the factory exists.

The factory's placement: `CurrentRolesPrincipalFactory` lives in Application, not Infrastructure, although D8 framed the principal-factory implementation as Infrastructure's. The factory does no I/O of its own. It composes the `ICurrentUserRolesStore` port and maps the result onto an `AuthenticatedPrincipal`; the I/O lives in the Postgres store behind the port. Application placement honors D8's spirit (the principal-factory concern is Application's, the storage is Infrastructure's), keeps the factory testable without a database, and avoids a new Infrastructure project for a type that touches no infrastructure.

The bus seam: the actor reaches the bus through a new principal-carrying `SendAsync` overload the endpoint calls with the actor and roles, not through an ambient principal accessor the bus reads. The overload mirrors the idempotency-key overload's placement and reasoning: the Api host resolves `ICommandBus` through DI and dispatches every user command through it, so the interface widens for the host edge. The bare overloads keep the actor empty for callers without a principal (worker writes, the System fallback), so the no-principal default and the unit tests asserting it stay unchanged. The overload is preferred over an accessor because the endpoint already holds the principal and the dependency is explicit at the call site rather than ambient.

The gated set: the two POST dispatch routes require authentication; the GET introspection routes stay open, because they publish only the catalog of accepted type tokens and no data. Queries are gated at the host (a request without an identity is a 401) but do not yet thread the actor into a query context, which is a later commit's concern.

## Extension: forwarded-identity signature (P9.3b)

P9.3b makes the forwarded header trustworthy across the Web-to-Api hop: the Web host signs the request and the Api host verifies the signature before it trusts the claim. This commit is the Api half, the enforced verification. It closes the P9.3a posture where the unsigned header was trusted only because a trusted upstream sat in front of it. The signature is now the credential, so an unsigned or wrongly-signed header is rejected whatever sits upstream.

The signature rides in its own header. `X-Forwarded-Identity` keeps its exact value, and `X-Forwarded-Identity-Signature` carries the base64url HMAC-SHA256 of that verbatim value under the shared secret. Folding a third segment into the value was rejected: the reader splits the value on its first semicolon and treats everything after as the roles segment, so an appended signature segment would land inside role parsing and couple the signature's safety to the role tokenizer. A separate header keeps the string the reader parses byte-identical to the string that was signed, which is the property the verification depends on. The handler verifies over the verbatim value and, only on success, hands that same value to the unchanged reader.

The secret is one configuration key both hosts read, never a serialized or persisted value. The Api host reads it with the same throw-on-missing idiom the connection strings use, and the verifier guards it in its constructor: a missing, blank, or under-length secret throws as the composition root constructs the verifier at startup, so a misconfigured deployment fails to boot rather than failing the first request. The floor is 32 characters. The secret never appears in a log, an exception message, or a comment. No new package: HMAC-SHA256 and the constant-time comparison are in the base class library.

Verification is fail-closed with no escape. There is no unsigned-acceptance path and no dev or test toggle that bypasses the check; the tests run against the same verification with a shared test secret. An absent signature, a signature that does not decode, and a signature that decodes but does not match all fail identically, and the comparison is constant-time so a wrong signature leaks no timing signal.

## Amendment: single-sourced wire contract and signing key (P9.3b commit 2a)

The header names, the value format, and the signing key are single-sourced in `Application/Authentication`, where both hosts already depend, so the Web signer and the Api verifier agree by construction rather than by a per-host duplicate kept honest by a test vector. `ForwardedIdentityHeaders` holds the two header names; `ForwardedIdentityValue.Format` produces the `{actorId:N};{roles}` value the reader parses, with an empty role set yielding the trailing-separator form `{actorId:N};`; `ForwardedIdentitySigningKey` holds the secret guard and the HMAC-SHA256 computation. The Api verifier composes the signing key rather than inlining the guard and the MAC, so the secret guard now fires as the composition root builds the key at startup, the same fail-fast timing as before. The signing key takes the `ForwardedIdentitySigningOptions` carrier directly rather than through `IOptions<T>`, so Application gains no options-package dependency for a type the composition root always constructs by hand. The scheme name stays in the Api host's `ForwardedIdentityDefaults`, since only the Api host registers the scheme.

This is a behavior-preserving refactor: the bytes on the wire and the verifier's accept and reject decisions are unchanged, which the gating tests prove green with their scenarios and assertions unmodified. The single source removes the drift risk the prior shape carried, where a change to the value format or the MAC on one host had to be mirrored on the other and was caught only if a shared test vector happened to exercise it.

## Trigger for revisiting

- A permission needs to vary per tenant or per deployment beyond what a static code-level policy expresses. That moves the policy to external configuration, keeping the same startup-validation guarantee.
- An audit requirement needs roles-at-time-of-action recorded in event metadata, which the current model deliberately excludes.
- The role taxonomy grows enough that a flat role-to-permission map needs hierarchy or composition, which the current flat enumeration does not carry.
- The Web-to-Api hop becomes reachable by a caller outside the shared trust boundary. The signature proves integrity, not freshness, so a captured header replays; the hop assumes a same-trust-boundary path over TLS. A boundary change moves the signed payload to carry a timestamp or nonce with a replay guard.
