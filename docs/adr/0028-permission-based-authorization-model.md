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

## Trigger for revisiting

- A permission needs to vary per tenant or per deployment beyond what a static code-level policy expresses. That moves the policy to external configuration, keeping the same startup-validation guarantee.
- An audit requirement needs roles-at-time-of-action recorded in event metadata, which the current model deliberately excludes.
- The role taxonomy grows enough that a flat role-to-permission map needs hierarchy or composition, which the current flat enumeration does not carry.
