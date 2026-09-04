# 0011. Typed StreamId With Type-Prefix Convention

## Status

Accepted (May 2026)

## Context

`IEventStore.AppendAsync` and `IEventStore.ReadStreamAsync` take stream identifiers as raw `Guid`; the PostgreSQL adapter's two corresponding methods carry the same shape. The `events.stream_id` column is `UUID NOT NULL` per migration `0001_initial_event_store.sql`, with the optimistic-concurrency constraint `uq_events_stream_version` on `(stream_id, stream_version)`. Four production signatures and one column type encode the current convention.

Phase 5 introduces process managers, each with its own state stream. The natural identifier for an `OrderFulfillmentProcessManager` instance is the `OrderId` it orchestrates, the same `Guid` as the Order aggregate's stream. Two streams sharing a `Guid` collide on the `(stream_id, stream_version)` constraint. The production-shape resolution is a type-prefixed identifier: a typed wrapper combining a short prefix naming the stream's role with the `Guid` identifying the instance. The convention has downstream benefits beyond Phase 5. DynamoDB's partition-key model consumes type-prefixed identifiers natively, and KurrentDB's category-stream model fits the convention with separator handling left to the Phase 13 adapter, so the Phase 13 and Phase 14 adapters inherit the right shape from the start.

An earlier scaffolding effort introduced `src/Domain.Abstractions/StreamNames.cs` as a static helper producing `{TypeName}-{guid:N}` with PascalCase aggregate type names. The helper has five methods (`ForAggregate<T>(Guid)`, `ForAggregate(string, Guid)`, `CategoryFor<T>()`, `ForPartition<T>(Guid, string)`, `SummaryFor<T>(Guid)`) and no callsites in production code or tests. This ADR supersedes and removes it.

## Decision

Stream identifiers in this reference implementation travel as a typed `StreamId` value object wrapping a string. The string format is `{prefix}:{guid:N}`, with `prefix` drawn from a curated list of lowercase identifiers: `order`, `inventory`, `shipment`, `payment` for aggregates; `pm-order-fulfillment`, `pm-return` for process managers. The separator is the colon character. The colon, rather than the hyphen the removed `StreamNames` used, keeps parsing unambiguous for hyphenated prefixes such as `pm-order-fulfillment`. Aggregate prefixes derive from the aggregate type via a `StreamId.ForAggregate<TAggregate>(Guid)` static factory; process-manager prefixes are passed explicitly via `StreamId.ForProcessManager(string pmType, Guid)`. The value object validates the format at construction and at `Parse`; malformed input throws.

`IEventStore`'s contract widens to use `StreamId` in place of `Guid` for stream identification on `AppendAsync` and `ReadStreamAsync`. The PostgreSQL adapter's column type changes from `UUID` to `TEXT`; the `uq_events_stream_version` constraint and its semantics carry over unchanged. `EventStoreRepository<TAggregate>` builds the stream ID via `StreamId.ForAggregate<TAggregate>`; PM repositories introduced by ADR 0012 build via `StreamId.ForProcessManager`.

`StreamNames` deletes outright. The file has no callsites; the deletion is single-file and dependency-free.

## Consequences

- The `events.stream_id` column migrates from `UUID NOT NULL` to `TEXT NOT NULL` in a schema migration. The `uq_events_stream_version` constraint reapplies to the text column. The `outbox` table has no `stream_id` column and is unaffected; the migration scope is `events.stream_id` alone.
- `IEventStore` and `PostgresEventStore` contract surfaces widen at four sites (two on each). The contract change and the `StreamId` value object land together, immediately ahead of the schema migration. The column migration carries the dependent callsites with it: `EventStoreRepository<TAggregate>`, the in-memory adapter, and every test that constructs stream identifiers.
- `src/Domain.Abstractions/StreamNames.cs` deletes outright alongside the contract change. The file has no dependents.
- ADR 0013 builds on the prefix convention for read-side payload-type routing. The `pm-` prefix family routes through `ProcessManagerEventTypeRegistry`; other prefixes route through `EventTypeRegistry`. Stream IDs that match no known prefix family fail loudly rather than falling back.
- Manuscript divergence against Chapter 9's Order aggregate code block and Chapter 13's projection code, both of which reference `Guid` stream identifiers. Phase 17 reconciliation updates the depicted shape to `StreamId`. The pedagogical content of both chapters survives the reconciliation unchanged.
- Phase 13 (KurrentDB) and Phase 14 (DynamoDB) adapters inherit the prefix convention from the start. The Phase 13 adapter maps the colon-delimited convention to KurrentDB's category form; the Phase 14 adapter uses the prefix directly. Phase 15 (snapshots) and Phase 12 (Correlation-ID Tracer) read stream IDs through the same `StreamId` surface that all other consumers use.

## Amendment: the tenant segment after the prefix (P10.2)

P10.2 fires the multi-tenant-partitioning condition this ADR's revisit triggers name. The stream-id string gains a tenant segment, so a non-default tenant's stream is `{prefix}:{tenant:N}:{guid:N}`. The prefix stays the leading segment and the colon stays the single delimiter, extended to a third segment rather than introducing a second separator type. That is the same property that chose the colon originally: the colon appears in neither a prefix nor a `Guid`'s `N` format, so a three-segment value parses by splitting on the colon with no ambiguity, and a tenant rendered in `N` form carries no colon of its own.

The tenant goes after the prefix, not before it, because the prefix is what the read side routes on. The `pm-` family is inspected at four leading-prefix sites: the projection-feed filter that excludes PM streams with `stream_id NOT LIKE 'pm-%'`, the two process-manager read guards that require a `pm-` prefix, and the idempotency-key guard that requires a process-manager stream. Tenant-after-prefix leaves the prefix where all four read it, so none of them change. A tenant-first format such as `{tenant:N}:{prefix}:{guid:N}` would push the prefix off the front and break all four at once. The ADR 0013 prefix-family routing this preserves is not amended, because the prefix stays exactly where 0013 reads it.

The default tenant has no tenant segment. Its streams render as the original two-segment `{prefix}:{guid:N}`, so the entire pre-tenancy corpus and every new default-tenant stream share one form and rehydrate without a tenant lookup, and no persisted stream id changes. `WellKnownTenants.Default` is the hand-fixed, non-empty constant the default tenant resolves to. The corpus-migration decision that records the two-format tolerance and that constant's permanence is ADR 0030.

`StreamId.IsWellFormed` validates structure only: the value splits into two or three colon-segments, the prefix segment is non-empty, and each guid segment is a compact-`N` guid. It does not check the prefix against a curated set. That is deliberate and unchanged from the original contract. Prefix-family identity is a read-side routing concern that lives with the routing, and `Domain.Abstractions` cannot enumerate aggregate prefixes, because they derive from the aggregate type name rather than from a curated map. The `ForAggregate` and `ForProcessManager` factories require a `TenantId`, threaded through every construction site; production sites pass the default tenant until the tenant source lands in a later commit.

The `uq_events_stream_version` constraint and the `TEXT` `stream_id` column are unchanged. The tenant rides inside the stream-id string, so the optimistic-concurrency constraint on `(stream_id, stream_version)` and the column type both carry over with no migration.

## Trigger for revisiting

The typed prefix convention is reversible. Conditions that would justify reopening it:

- A future event-store adapter cannot represent string-typed stream identifiers, forcing the contract back to `Guid` or a different wrapping. The relational and document-store adapters this codebase plans for all accept strings; a hypothetical adapter that doesn't would trigger the question.
- A future pattern requires a richer stream-identifier type than `{prefix}:{guid:N}` can carry: multi-tenant partitioning, hierarchical streams, version-suffixed identifiers. The current shape covers the codebase's roadmap through Phase 15; needs beyond that would trigger the question.
