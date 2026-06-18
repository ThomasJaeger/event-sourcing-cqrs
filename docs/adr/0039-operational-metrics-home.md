# 0039. Operational Metrics Home

## Status

Accepted (June 2026).

## Context

Phase 11 shipped the customer-adjacent Web host throughput meter at /admin/throughput (commit e882b23). Phase 12 (AdminConsole) names a Projection Status Dashboard requiring per-projection checkpoint, lag in seconds, and last error, alongside operational tools over the event store and the outbox. The operational metrics (projection lag, outbox depth, events per second) raise a home question: the Web host, where the throughput meter already lives, or the AdminConsole host, the operator surface. The projection-lag substrate, read_models.projection_checkpoints, is global rather than tenant-scoped, which is correct: lag is an operational property of the projection worker and carries no tenant business data.

## Decision

Operational metrics live in the AdminConsole host, born at the Projection Status Dashboard. They stay out of the Web host throughput meter and every customer-adjacent Web surface. The operational reader (projection lag, outbox depth) is built as the first slice of Phase 12, born at its Projection Status Dashboard consumer, rather than as a standalone pre-Phase-12 slice: it has one genuine consumer, so building it ahead of that consumer would be abstraction ahead of need. It is tested standalone as its own RED and GREEN slice inside Phase 12 before the dashboard consumes it.

## Consequences

The Web host stays scoped to customer-facing and tenant-scoped admin views; operational and cross-tenant metrics concentrate in the AdminConsole. A future slice that adds an operational tile to the Web host diverges from this decision and should be caught in review. The global checkpoint table is the correct substrate for lag and needs no tenant re-keying.

## Revisit when

An operational metric turns out to be tenant-scoped (a per-tenant operational view), at which point its home and scoping are decided against this record rather than by default.
