# 0030. EventEnvelope Tenant and the Corpus Migration

## Status

Accepted (May 2026). Opened at P10.2 for the stream-id and corpus-migration groundwork; extended at P10.3 when the `EventMetadata` tenant field and the events-table migration land.

## Context

Multi-tenancy by a shared-schema discriminator (the tenant-isolation ADR) requires the tenant to appear in the stream id, in event metadata, and on every read-model row. The existing event corpus predates tenancy: every persisted stream id is two-segment and no event metadata carries a tenant. Events are immutable in this system, never mutated and never rewritten, and an aggregate's stream id cannot change mid-stream because rehydration reads by stream id. A migration strategy must add the tenant without rewriting a single historical stream id or event fact.

## Decision

The migration is bi-format-additive. Historical rows are physically untouched. Their two-segment stream ids stay, and `StreamId.Parse` tolerates the two-segment form, resolving it to the default tenant (`WellKnownTenants.Default`, a hand-fixed non-empty constant in `Domain.Abstractions`). New non-default-tenant streams are three-segment. The default tenant is the absence of a tenant segment, so pre-tenancy aggregates and new default-tenant aggregates are uniformly two-segment and continue appending under their stable ids with no rehydration-key rewrite.

The tenant lands in `EventMetadata` as a typed `TenantId`, not in the event payload, so there is no payload threading and no entanglement with the snapshot upcasting path. `EventMetadata` is shared by both event envelopes, so process-manager events carry the tenant the same way; a process manager orchestrates within one tenant. The metadata serializer maps a legacy event without a tenant to the default tenant on read. P10.3 lands this field and the serializer mapping; this ADR opens the decision and P10.3 completes it.

The events table and every read-model table gain an indexed `tenant_id` column, backfilled to the default tenant by an additive `UPDATE` consistent with the existing migration practice, not a payload or fact rewrite. P10.3 lands the events-table column and backfill; the read-model columns land at the read-isolation commit.

## Consequences

- Two stream-id formats coexist in the events table: the legacy two-segment default-tenant form and the three-segment non-default form. This is a bounded parser tolerance in `Parse`, recorded rather than hidden. It is the honest shape of a system that adopted tenancy mid-life and carries a legacy default-tenant cohort.
- No historical stream id or event fact is rewritten, so the append-only and immutable-event disciplines are honored exactly.
- The default-tenant constant must never drift, because the entire legacy corpus resolves to it permanently. It is a hand-fixed literal, mirroring the system-actor constants, not a generated value.
- The tenant is derivable from both the stream id (via `Parse`) and, once P10.3 lands, the metadata. The events-table `tenant_id` column is an operational-indexability denormalization for tenant-filtered admin queries and per-tenant replay, not a correctness requirement of the write or projection path.

## Relationship to other ADRs

ADR 0011 is amended for the tenant-after-prefix stream-id format. ADR 0029 defines the typed `TenantId` this migration keys off. The tenant-isolation ADR (the discriminator model) is the parent decision this migration serves. ADR 0013 prefix-family routing is unchanged.
