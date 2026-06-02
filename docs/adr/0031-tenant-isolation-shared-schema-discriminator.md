# 0031. Tenant Isolation by a Shared-Schema Discriminator

## Status

Accepted (June 2026). The parent decision ADR 0029 and ADR 0030 forward-referenced; recorded now that the foundation through P10.4 has landed. The read-isolation enforcement mechanism, left open at acceptance, is decided and recorded below as of P10.5.

## Context

Multi-tenancy ships in v1 (PLAN.md's amended scope). The isolation model is a shared-schema discriminator: one database, one schema, every tenant's data in the same tables, separated by a `tenant_id` discriminator rather than by a database or a schema per tenant. The tenant appears at more sites than any other identifier in the system: a `tenant_id` column on every read-model table and an indexed one on the events table, a tenant component in the stream id, the tenant on the runtime principal, and the tenant in event metadata.

A shared-schema discriminator trades isolation strength for operational simplicity. The cost is that isolation is a predicate, not a boundary the storage engine enforces: a read that omits the tenant predicate returns another tenant's rows. A one-line omission is a cross-tenant read, a security incident rather than an ordinary defect. The model therefore cannot rest on per-query discipline. It depends on read isolation enforced by infrastructure and on cross-tenant coverage that is structural rather than reviewer-dependent.

## Decision

The isolation model is the shared-schema discriminator. The tenant is a typed `TenantId` (ADR 0029), an isolation dimension the type system keeps distinct from every other identifier, so a structurally invalid tenant cannot reach a discriminator predicate.

The following have landed and are settled:

- The tenant in event metadata as a typed `TenantId`, and the events-table `tenant_id` column derived from metadata by a STORED generated column, with the corpus migrated bi-format-additively and no historical fact rewritten (ADR 0030).
- The tenant component in the stream id, tenant-after-prefix so prefix-family routing is unaffected, with `Parse` tolerating the legacy two-segment default-tenant form (ADR 0011 as amended, ADR 0030).
- The current-tenant accessor. `ICurrentTenantAccessor` lives in `Domain.Abstractions`, async-local-backed by `AsyncLocalCurrentTenantAccessor` in `Application/Context`, and is the single source of the current tenant on an async flow. It is read where the event tenant and the stream id are constructed (P10.4). It is set from the principal at the HTTP edge and from the causing event's metadata on the caused-command path. P10.5 adds the read-model `tenant_id` columns and reads the accessor at the query-side set-point and the subscription-authorization set-point to enforce read isolation, as recorded below. The projection and outbox worker-edge set-points and the process-manager stream-identity tenant land at their own slices (P10.6 and the process-manager propagation slice); until then the projection write path and those worker paths resolve to the default tenant.

The accessor's placement follows the consumer-set-asymmetry rationale ADR 0026 records: a port the engine-agnostic repositories and the later read-isolation path depend on lives in `Domain.Abstractions`, below `Application`, while the principal factory that populates it from a read model stays in `Application`, the same split ADR 0028 made for `CurrentRolesPrincipalFactory`. The port is low because consumers below `Application` reach it; the factory is in `Application` because it does no I/O of its own.

The tenant rides on the principal alongside the actor and the actor's roles (ADR 0028, as amended). The principal factory sets it, to the default tenant until a per-user tenant lookup arrives with tenant onboarding.

The write path fails closed. A command context in flight with no tenant set on the async flow throws `MissingTenantContextException` rather than stamping the default tenant. The command bus sets the tenant accessor in the same block as the command context, so a present context with an unset tenant is a dispatch-wiring regression, and the write path surfaces it rather than writing a silently-defaulted event. Off the command path (worker writes, the no-context fallback) the default tenant is the honest value and is stamped explicitly.

## Consequences

- Every tenant's data shares one set of tables. Isolation is a `tenant_id` predicate, so correctness depends on that predicate being present at every read, subscription, and projection. This is the discipline-dependent risk the model carries, recorded rather than hidden.
- The cross-tenant coverage mandate is the safeguard the model depends on. No query, command, subscription, or projection reaches production without a cross-tenant isolation test, enforced structurally so a registered type that lacks coverage fails the suite. The mandate is the model's load-bearing safety net, not an optional add-on.
- The default tenant is the absence of a tenant segment and the pinned `WellKnownTenants.Default` constant, so the legacy corpus and every new default-tenant stream resolve uniformly until a second tenant exists.

## The read-isolation enforcement mechanism

The enforcement is a per-store tenant predicate on every query-path read-model read, decided at P10.5 against the read-model connection shape on disk. Each query-path read resolves the current tenant from the accessor as its first action through one shared guard, throws when no tenant is set, and filters its query on the `tenant_id` discriminator. The guard centralizes the fail-closed throw in one place; the predicate lives in each read because the read-model stores share no query-composition seam to inject through.

Row-level security keyed on a per-connection session variable was the lean at acceptance and was examined first. Two facts on disk decided against it. The application connects to PostgreSQL as the schema owner, and a row-level-security policy does not constrain the owning role unless forced; forcing it would require a separate non-owner application role with its own grants and a second connection identity, an operational surface the system does not carry. And the read-model connection factory is shared by the projection write path, which has no tenant set until its own slice, so a connection-level policy would reach writes that must not yet be constrained and would break or silently filter projection work. A per-connection session variable does reset cleanly on pooled return under the shipped driver, pinned by an integration test, so row-level security stays available as a future defense-in-depth layer once a non-owner role exists; it is not the enforcement mechanism.

The predicate is not a per-author vigilance requirement. The cross-tenant coverage mandate is the safeguard that makes it production-grade: every query handler carries a cross-tenant test that seeds two tenants and asserts a read as one returns none of the other's rows, and the structural coverage harness fails the suite if a registered query type lacks that test, so a new query that ships without isolation coverage fails the build rather than slipping past review.

The read isolation enforced at P10.5 covers the query-path reads: the read-model store reads reached through the query bus, and the order-detail header read reached by the subscription-authorization endpoint, which sets the tenant from its principal the same way the bus does. The read paths that do not yet carry a tenant are enforced at the slices where their tenant arrives: the projection-write private lookups and the tenant-scoped SKU keys at P10.6, the process-manager lookup reads at the process-manager propagation slice, and the user-roles read at the per-user-tenant slice. Until then those paths resolve to the default tenant, which is the honest value while a single tenant exists.

## Relationship to other ADRs

ADR 0029 defines the typed `TenantId` this model keys off. ADR 0030 records the event-metadata tenant, the events-table column, and the corpus migration this model's persistence rests on. ADR 0011 carries the tenant-after-prefix stream-id format. ADR 0026 supplies the port-placement asymmetry the accessor placement follows. ADR 0028 carries the tenant on the principal. The read-model `tenant_id` columns and the query-path read-isolation enforcement land at P10.5; the projection-side tenant tagging and the tenant-scoped SKU keys land at P10.6; the tenant-qualified hub groups and the notification envelope tenant land at P10.7; the outbox, delay-queue, and process-manager tenant propagation land at their own slices.
