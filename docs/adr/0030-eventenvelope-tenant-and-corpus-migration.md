# 0030. EventEnvelope Tenant and the Corpus Migration

## Status

Accepted (May 2026). Opened at P10.2 for the stream-id and corpus-migration groundwork; extended at P10.3, which landed the `EventMetadata` tenant field, the serializer mapping, and the events-table migration.

## Context

Multi-tenancy by a shared-schema discriminator (the tenant-isolation ADR) requires the tenant to appear in the stream id, in event metadata, and on every read-model row. The existing event corpus predates tenancy: every persisted stream id is two-segment and no event metadata carries a tenant. Events are immutable in this system, never mutated and never rewritten, and an aggregate's stream id cannot change mid-stream because rehydration reads by stream id. A migration strategy must add the tenant without rewriting a single historical stream id or event fact.

## Decision

The migration is bi-format-additive. Historical rows are physically untouched. Their two-segment stream ids stay, and `StreamId.Parse` tolerates the two-segment form, resolving it to the default tenant (`WellKnownTenants.Default`, a hand-fixed non-empty constant in `Domain.Abstractions`). New non-default-tenant streams are three-segment. The default tenant is the absence of a tenant segment, so pre-tenancy aggregates and new default-tenant aggregates are uniformly two-segment and continue appending under their stable ids with no rehydration-key rewrite.

The tenant lands in `EventMetadata` as a typed `TenantId`, not in the event payload, so there is no payload threading and no entanglement with the snapshot upcasting path. `EventMetadata` is shared by both event envelopes, so process-manager events carry the tenant the same way; a process manager orchestrates within one tenant. The metadata serializer maps a legacy event without a tenant to the default tenant on read, through a single read seam that coalesces an absent or null tenant to `WellKnownTenants.Default`. P10.3 landed this field and the serializer mapping; this ADR opened the decision and P10.3 completed it.

The events table gains an indexed `tenant_id` column, and every read-model table gains one in turn. P10.3 landed the events-table column at migration 0016 as a STORED generated column projecting `metadata->>'tenant_id'` with a `COALESCE` to the default tenant, the same idiom the `correlation_id` and `causation_id` columns use. The column materializes for every existing row at `ALTER` time, deriving each legacy row's default from its own metadata, so no historical fact is rewritten and no backfill `UPDATE` is needed; the additive discipline is honored by derivation rather than by a data-shaping write. The read-model `tenant_id` columns land at the read-isolation commit.

## Consequences

- Two stream-id formats coexist in the events table: the legacy two-segment default-tenant form and the three-segment non-default form. This is a bounded parser tolerance in `Parse`, recorded rather than hidden. It is the honest shape of a system that adopted tenancy mid-life and carries a legacy default-tenant cohort.
- No historical stream id or event fact is rewritten, so the append-only and immutable-event disciplines are honored exactly.
- The default-tenant constant must never drift, because the entire legacy corpus resolves to it permanently. It is a hand-fixed literal, mirroring the system-actor constants, not a generated value.
- The tenant is derivable from both the stream id (via `Parse`) and the metadata. The events-table `tenant_id` column is an operational-indexability denormalization for tenant-filtered admin queries and per-tenant replay, not a correctness requirement of the write or projection path; no current read path selects it. The default-tenant Guid is now pinned in two places, the `WellKnownTenants.Default` constant and migration 0016's `COALESCE` literal, both immutable in practice (the constant is hand-fixed and tested, the literal is frozen in an applied, checksummed migration).

## Relationship to other ADRs

ADR 0011 is amended for the tenant-after-prefix stream-id format. ADR 0029 defines the typed `TenantId` this migration keys off. The tenant-isolation ADR (the discriminator model) is the parent decision this migration serves. ADR 0013 prefix-family routing is unchanged.
