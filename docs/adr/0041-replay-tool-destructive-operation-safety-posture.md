# 0041. Replay Tool Destructive-Operation Safety Posture

## Status

Accepted (June 2026). Establishes the safety posture for the AdminConsole Replay Tool, the operator-facing per-tenant projection rebuild. Builds on ADR 0040's AdminConsole authorization gate and ADR 0028's per-permission grain.

## Context

Phase 12 adds the Replay Tool, the second data-bearing AdminConsole page after the Projection Status Dashboard (ADR 0039 and ADR 0040). It lets an operator rebuild a projection's read model for one tenant by clearing that tenant's rows and replaying the tenant's events.

A rebuild is a destructive operation. Done wrong it can leave a read model half-rebuilt with an advanced checkpoint, so the projection reports it has processed events it has dropped, and the read model never recovers without manual intervention. The posture for this hazard is settled here before the tool ships.

The codebase already holds a safe rebuild primitive. PerTenantProjectionRebuilder runs a rebuild over a checkpoint-neutral RebuildModeCheckpointStore: its position reads floor to a sentinel so no replayed event is skipped, and its advance is a no-op so the shared global checkpoint row never moves during a rebuild. It clears the target tenant through ITenantResettable.ResetTenantAsync and drives a bounded per-tenant replay through ProjectionReplayer.ReplayForTenantAsync, with the live global position read once as a replay ceiling. Six read-model stores implement ITenantResettable.

The order-throughput read model is the gap. Its table read_models.order_throughput is tenant-discriminated by a composite primary key of tenant_id and second_utc, and its projection participates in the shared global checkpoint mechanism like every other projection. PostgresOrderThroughputStore is the one tenant-scoped store that does not implement ITenantResettable. It carries instead a TruncateAsync that drops every bucket for every tenant, wired as a no-op placeholder with zero callers anywhere in the source tree. Its contract comment promises the method empties the table for a replay; the implementation returns a completed task and empties nothing.

## Decision

The Replay Tool rebuilds one tenant at a time through the existing PerTenantProjectionRebuilder. The throughput store joins the established safe path: PostgresOrderThroughputStore implements ITenantResettable.ResetTenantAsync to clear one tenant's buckets, and the tool drives the rebuilder against the throughput projection. The rebuilder's checkpoint-neutral construction gives the safety property by structure: the real global checkpoint never moves during a rebuild, the blast radius is one tenant, and a failure mid-rebuild recovers on a clean re-run because reset-then-replay is idempotent over the read model's upsert key.

The operator action is gated by a dedicated rebuild permission, layered as an action policy over the AdminConsole host fallback of ADR 0040. The permission is minted at this consumer per ADR 0028's prescription that the enumeration extends as required permissions are reconciled. The Admin role holds it through the computed-all role-to-permission policy. The grain exists so a future operator role below Admin is closed to the rebuild by default and gains it only by explicit grant, the deny-by-default reasoning ADR 0040 applied to page access carried down to the action.

The destructive action carries a confirmation step in the operator flow before it runs.

TruncateAsync is removed from IOrderThroughputStore, from PostgresOrderThroughputStore, and from the in-memory fakes that carry it. Its sole stated purpose was rebuild support, now served by ResetTenantAsync, and it had no caller. A contract method whose implementation cannot honor the behavior its contract promises is a correctness defect in a reference implementation that readers run in production: a reader wiring a rebuild against the documented TruncateAsync would clear nothing and double-count on replay. The method does what it says or it does not exist; under this decision it does not exist.

Rejected alternatives. Whole-table truncate-and-replay across all tenants was rejected: it builds a destructive all-tenant primitive that no consumer needs, it diverges from the per-tenant path the codebase converged on, and a naive whole-table replay against the real global checkpoint reintroduces the half-rebuilt-with-advanced-checkpoint hazard, so making it safe would reimplement the checkpoint-neutral machinery at whole-table grain for no caller. An all-tenant rebuild, when a consumer needs one, composes from the per-tenant primitive iterated over tenants. Building both paths was rejected for the same reason: it still ships the unused destructive surface. Gating the action at the existing AccessAdminConsole grain was rejected: it leaves the rebuild open to any console operator the moment a non-Admin operator role appears, the opt-in-to-tighten posture ADR 0040 rejected for pages. Keeping TruncateAsync as a documented no-op was rejected: shipped production code does not carry a permanent no-op standing in for a contract method.

## Consequences

The throughput store stops being the lone tenant-scoped store without ITenantResettable, and the Replay Tool becomes the first production consumer of PerTenantProjectionRebuilder, reachable until now only from tests. The rebuild's safety is inherited from the checkpoint-neutral path rather than built fresh. The permission enumeration gains one member, auto-granted to Admin and withheld from any role below it. The AdminConsole acquires a real IEventStore read for the first time, since the replay path reads the event stream; that composition is a focused-registration decision carrying the ValidateOnBuild over-provisioning risk the authorization-gate arc met, resolved at the page slice rather than here. The all-tenant rebuild stays out of scope until a consumer needs it. The two RED #4 placeholder headers on PostgresOrderThroughputStore and its unit of work are rewritten to the finished state when TruncateAsync is removed.

## Revisit when

A consumer needs an all-tenant rebuild, at which point it iterates the per-tenant primitive over a tenant-enumeration source decided at that consumer. Or an operator role below Admin is introduced, at which point the rebuild permission's grant is decided for that role against this record.
