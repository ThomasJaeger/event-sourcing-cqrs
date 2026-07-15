# 0047. KurrentDB Adapter Engine Mappings

## Status

Accepted (July 2026)

## Context

Phase 13's KurrentDB adapter is the first non-relational implementation of
IEventStore (ADR 0004 self-containment; ADR 0044 ordering contract), and the
first to differ from PostgreSQL on more than one axis at once: a gRPC client
rather than a SQL driver, native catch-up subscriptions rather than an outbox
drain, and the engine's own position and concurrency semantics. A throwaway spike
against KurrentDB 26.0.2 (TDD_RULES section 1) established the engine's real
contract before authoring, and several session spikes across the arc pinned
behaviors that no amount of reading would have surfaced. This ADR records the
mappings and the decisions they forced, on the model of ADR 0045. The engine is
run insecure with an in-memory database for tests, reached over an esdb:// string
with tls=false; the version floor is 26.0.2, provable in CI.

## Decisions

**Commit-ordered visibility (ADR 0044).** KurrentDB gives the invariant for free
where the relational adapters buy it. Positions are assigned at commit by the
single-writer $all log, so commit order is the total order with no added lock; the
PostgreSQL and SQL Server adapters take an append lock so an IDENTITY draws in
commit order, but the log has that property intrinsically. Gaps come from the
log's own structure rather than from rolled-back position draws, which ADR 0044
already tolerates as permanent. The contract suite's ordering fact runs against the
$all feed and its held-writer probes route away from this engine, because KurrentDB
has no interactive transaction to park an append mid-flight; a concurrent-append
load probe drawing strictly ascending, non-colliding committed positions stands in
their place (the engine-semantics class pins it). ADR 0044's July 2026 amendment
recorded the SQL Server posture; the KurrentDB posture appends to that record,
recording an engine that satisfies the invariant natively.

**Position shape and the ulong surface.** A KurrentDB Position carries a commit
and a prepare position, both ulong; the port speaks a single long global position.
The adapter maps the commit position to the port with a checked cast,
checked((long)record.Position.CommitPosition), so a log that ever exceeds long.Max
fails loudly rather than wrapping negative. The reverse map reconstructs a Position
from the port's long with the same value for both coordinates,
new Position((ulong)fromPosition, (ulong)fromPosition), and a forward read from a
Position is inclusive, so exclusive-resume is a client-side skip on the commit
position rather than a property of the read. The prepare coordinate is not
preserved across the port boundary; the reconstruction is sound because the $all
resume and the head read both order on the commit position alone.

**Identity naming and concurrency.** StreamId.Value is the KurrentDB stream name
in both directions, so no translation layer sits between the port and the wire
(ADR 0011). Optimistic concurrency is the engine's native expected-revision check:
expectedVersion is the port's one-based event count, mapping to StreamState.NoStream
for an empty stream and StreamState.StreamRevision(expectedVersion - 1) otherwise,
because KurrentDB revisions are zero-based while the port's stream versions are
one-based. A WrongExpectedVersionException translates to the contract's
ConcurrencyException. There is no uniqueness constraint to name and no error number
to filter, so the concurrency translation is a single typed catch rather than the
relational adapters' number-and-substring filter.

**Native duplicate-id idempotency.** KurrentDB deduplicates a retried append by
event id at the stream, so the adapter passes the contract's event ids straight
through as KurrentDB event ids rather than minting fresh ones (session spike). This
is idempotency by whole-append, not by event id within one append: two envelopes
sharing an id in a single AppendAsync both land, and the same id appended to two
streams lands on both, because the dedup keys per stream. The engine-semantics
class pins all three as green-on-write characterizations, each naming the shipped
change that would turn it red, because the adapter's identity mapping relies on
them.

**JSON storage.** The payload rides the KurrentDB data slot; everything the engine
cannot reconstruct on read, the schema version, the occurred time, and the domain
metadata, rides the metadata slot as a StoredEventMetadata record. Both serialize
with the adapter's own JsonSerializerOptions, its own file per ADR 0004, copied
from the relational shape so a payload serialized on any engine round-trips on any
other: snake_case_lower naming, TenantIdJsonConverter for the flat tenant scalar,
and the pinned JavaScriptEncoder.Default. The store's stream reads and the
subscription's dispatch share one hydration path, so both reconstruct an event
through one seam.

**Subscription dispatch and checkpoint tail-lag.** KurrentDB feeds projections
through a native catch-up subscription over the filtered $all feed in place of an
outbox drain (PLAN.md:454). The subscription service plays each matched event into
the same in-process dispatcher the relational outbox processors feed, advancing its
own $all dispatch checkpoint, held under a name disjoint from every projection
checkpoint. The loop resumes exclusively from the stored checkpoint on reconnect
(FromAll.After, exclusive), so dispatch is at-least-once and per-handler idempotency
absorbs a repeat, mirroring the outbox processors' back-off-and-retry posture. A
session spike refuted the assumption that a short filtered tail advances the stored
checkpoint: the server emits an AllStreamCheckpointReached past a filtered stretch
only above its own catch-up batch threshold, so below it the checkpoint lags at the
last dispatched position. That lag is correct, because the server-side filter
replays the tail as skips on reconnect, and a lag metric reading this checkpoint
shows honest staleness through a filtered quiet period rather than a false zero. The
subscription fact that pins the checkpoint passing a filtered stretch was reshaped
to a middle stretch (aggregate, PM events, aggregate) so the next aggregate's
per-event advance carries the checkpoint past the stretch, testing the intent
without depending on the undocumented batch threshold.

**Aggregate-filtered head, and its divergence from PostgreSQL.** The AdminConsole's
projection-lag read needs the head of the event stream. The KurrentDB head reader
reads $all backwards from its end, one event, through the same server-side filter
the store's reads use, ^(?!\$)(?!pm-), so the head lands in the same commit-position
space as the projection checkpoints it is subtracted from. A session spike
established that a raw backwards read returns a KurrentDB system event on an
otherwise empty node, so the filter is load-bearing: an empty aggregate feed yields
no event and maps to 0, matching PostgresEventStoreHeadReader's COALESCE-to-0. This
diverges from the relational reader on PM rows. The PostgreSQL head is
MAX(global_position) over the shared events table, which holds aggregate and PM
rows, so a PM-tailed log reports the PM row as head there while the KurrentDB head
excludes it. Both readers land the head in the projection checkpoints' own view of
the log, so neither inflates projection lag with events the projections do not
process; the divergence is in what "head" spans, not in the sign of the lag.

**The Correlation-ID Tracer's designed unavailable state.** The tracer reads the
events by a correlation_id index that only the relational schema carries, a STORED
generated column indexed by migration 0001. KurrentDB has no cross-stream metadata
index; a correlation trace would be a dedicated user projection, deferred. The
AdminConsole composes a defense-in-depth ICorrelationTraceReader that throws the
named reason, registers a capability marked unavailable-with-reason, and gates the
/correlations page behind a notice so the throwing reader is never reached in normal
operation. This is the capability-split shape for an engine that cannot express a
port the abstraction assumes: name the unavailability, gate the surface, keep a
throwing implementation behind the gate rather than a silent wrong answer.

**Registration posture: TryAdd throughout.** The adapter registers every service
with TryAdd, a deliberate divergence from AddPostgresEventStore's plain Adds. A
KurrentDB host composes the adapter into a host that also composes the read-model
side against the same PostgreSQL database and, on the AdminConsole, a head reader
over the same client, so registering defensively keeps a shared dependency from
being double-registered across those seams. The relational adapters own the sole
bare data source in their hosts and register with plain Adds.

## Consequences

- The switching guarantee at PLAN.md:253 extends to KurrentDB for the write hosts
  (Api, Workers) and the read host (AdminConsole) by EVENT_STORE_PROVIDER selection,
  through a third copy of the per-host parser twin.
- ADR 0004's revisit trigger, three adapters touching identical code, has fired.
  The KurrentDB adapter carries its own copy of the type registries and the JSON
  seam beside the two relational copies. The registries' own comments name
  Infrastructure/Versioning in Phase 15 as the collapse point; the collapse is the
  named open question the third copy hands to Phase 14/15, against ADR 0045's
  corrected 234-line figure.
- The projection-lag reading on a KurrentDB deployment carries the aggregate-only
  head; the tail-lag dispatch checkpoint is a separate position and does not feed
  the projection lag, but shares the filtered-tail shape and is recorded here as an
  operator-visible characteristic.
- The InMemoryEventStore used by the Web host and some tests stays outside the
  contract suite, so its behavior is not held to the engine mappings this ADR
  records; a recorded residual.

## Trigger for revisiting

Version-conditional adapter behavior reopens the CI matrix. The third-copy registry
seam collapses at Phase 15's Infrastructure/Versioning, or earlier if Phase 14's
fourth adapter forces it. An engine-native correlation index, or a dedicated
correlation projection, would reopen the tracer's unavailable state and be its own
ADR. A native scheduler in place of the reused delay queue reopens ADR 0046.
