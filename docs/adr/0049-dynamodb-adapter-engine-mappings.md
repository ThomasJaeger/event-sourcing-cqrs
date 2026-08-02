# 0049. DynamoDB Adapter Engine Mappings

## Status

Accepted (July 2026)

## Context

Phase 14's DynamoDB adapter is the fourth IEventStore implementation (ADR 0004
self-containment; ADR 0044 ordering contract) and the first whose engine offers
no global sequence, no interactive transaction, and no ordered feed to subscribe
to. Where KurrentDB differed from PostgreSQL on several axes at once, DynamoDB
differs on the ones the ordering contract is built from, so most of this arc's
work was buying ADR 0044's guarantee out of primitives that do not carry it. A
throwaway spike against LocalStack 4.14.0 (TDD_RULES section 1) established the
engine's real contract before authoring, and several session spikes across the
arc refuted things reading alone had gotten wrong. This ADR records the mappings
and the decisions they forced, on the model of ADR 0047. The run posture is
LocalStack only, per PLAN.md's Phase 14 out-of-scope entry "Real AWS deployment.
Local-only via LocalStack."

## Decisions

**Commit-ordered visibility (ADR 0044).** There is no engine-assigned global
sequence, so the adapter draws positions itself: one counter row, read then
advanced by a conditional update inside the append's own TransactWriteItems
transaction. The condition is current = P, which serializes position assignment
across every writer in the deployment no matter which stream it writes. That
counter is the design's price, and it is not a tuning problem to optimize away
later: a spike measured twelve writers landing 300 appends in 1667 attempts, 82
percent of them cancelled, at roughly 19 appends per second. A deployment
needing more write throughput than one global position can hand out needs a
different ordering guarantee, not a bigger table.

**The retry cap, and the math that sized it.** A writer that loses the counter
race retries; a cap makes one that keeps losing fail loudly rather than spin.
Among N contenders exactly one wins a round, so a given writer loses with
probability (N-1)/N. At the spike's twelve writers, (11/12)^64 is 3.8e-3, about
one append in 262, which over a 300-append run is a two-in-three chance of
starving somebody: a coin flip, not a cap. (11/12)^256 is 2.1e-10, negligible by
construction rather than by luck, so the cap is 256. The model is a worst case,
since backoff desynchronizes writers and stragglers drop out, and it overstates
the real rate by roughly eight times: 64 starved about one CI run in seven rather
than two in three. It is mildly pessimistic and safe to reason from, which is
what a sizing model owes. Attempts rather than milliseconds is the unit, because
attempts are what the engine charges for and a wall-clock budget would mean a
different cap on every machine. CI proved that hazard: 64 survived a 32-core box
five runs out of five and starved on a 4-core runner, same code and same
LocalStack, because scheduling widened the window between reading the counter and
writing it.

**Full-jitter backoff, repaired.** The retry backoff shipped linear, saturating
every writer into a 25ms to 39ms band by the thirteenth attempt, so twelve
writers that collided once retried inside the same 14ms window and collided
again. A policy whose whole job is to pull contenders apart was holding them
together, and that was the disease the cap was the symptom of. It now carries
OutboxRetryPolicy's full-jitter exponential shape at append-path durations, a 2ms
base and a 50ms ceiling, milliseconds where the outbox spends seconds.

**Gaps, inverted.** ADR 0044 tolerates permanent gaps because a rolled-back
relational append burns the positions it drew: PostgreSQL IDENTITY and SQL
Server's identity cache both hand out numbers that never come back. This engine
cannot burn one. A position is drawn by a conditional counter update inside the
append's own transaction, so a cancelled transaction rolls the increment back
with everything else and the next writer draws the same number. The suite pins
gapless here, stronger than the contract requires, and a gap would be a defect
rather than a tolerated engine behavior. The spike observed the same: 300
appends, positions 1 to 300, none missing.

**The dead GSI, and the log partition that replaced it.** PLAN.md's Phase 14 GSI goal proposed a
Global Secondary Index for global ordering and replay. The live engine refuses
it: ConsistentRead against a GSI is rejected outright with "Consistent reads are
not supported on global secondary indexes" (ValidationException). A GSI-backed
position therefore cannot serve the strongly-consistent ordered read ADR 0044
requires, so ordering rides a log partition on the base table, which
ConsistentRead does serve: one row per committed event under a single partition
key, the position as its sort key, read in sort order. The partition is hot by
construction, which is the shape the engine's own guidance says to avoid, and it
is here because the alternative does not work. LocalStack would not have caught
the refutation on its own: it propagates GSI writes synchronously, so a GSI-based
design reads correct locally and diverges on real AWS, which makes the emulator a
fidelity trap on exactly the axis this decision turns on. The refutation came from
the API's own rejection, not from a consistency observation.

**The item budget, and the third item per event.** TransactWriteItems accepts 100
items, measured: 101 are rejected with "Member must have length less than or equal
to 100" (ValidationException). An append spends 1 + 3n items, the counter update
plus an event row, a log row, and an event-id row per event, so 33 events fit
exactly and 34 do not. The guard is loud and pre-write: an oversized append raises
DynamoDbAppendTooLargeException naming the count, the item count, and the limit,
before anything is sent, rather than surfacing as an opaque engine validation
error. The event-id row is what buys the third capability class. The engine has no
opinion about a reused event id, so the adapter gives it one: an existence-checked
row per event id inside the same transaction, which makes a reused id fail the
append rather than land twice. That is why this engine derives the
duplicate-rejection suite where KurrentDB, whose append is natively idempotent by
event id, cannot.

**Held-writer probes routed away, and what stands in.** DynamoDB has no
interactive, held-open write. The client's only transaction surface is
TransactWriteItems, a one-shot request resolved server-side from its whole item
list, with no Begin, Commit, or Rollback anywhere; ClientRequestToken is an
idempotency window, not a held transaction. A backend whose held writer was a
no-op would make those probes vacuous, so this engine derives the universal suite
and the duplicate-rejection suite but not the held-writer one, two of three, on
the KurrentDB precedent. The concurrent-append storm probe stands in for the
ordering evidence they would have carried, and it carries more here than it does
on KurrentDB: it is the only fact that drives the retry loop, the backoff, and the
positional cancellation translation under real contention.

**The log row carries the whole envelope, and that is a dual write.** Each event
is written twice, once under its stream partition and once under the log
partition, because the alternative is a pointer the feed would have to chase per
event. ReadAllAsync is then one Query on one partition rather than a fan-out of
per-stream reads, which is what makes the hot partition worth its cost. The
duplication is the trade and it is paid on every append. Tenant filtering rides
the same read: the tenant is lifted out of the metadata JSON to a top-level
attribute so a tenant-window read filters server-side, but the honest scope is
narrow. A FilterExpression runs after the page is read, so it saves transfer and
deserialization and not read capacity: the RCU is the window's, not the tenant's.
Narrowing capacity would need the tenant in a key, and no key on this table can
carry it without giving up the single ordered feed.

**The head is unfiltered, and the three engines now split two ways.** The log
partition carries process-manager rows as well as aggregate rows, because the
committed-position sequence spans both append paths, so the PM exclusion
elsewhere is a read-side filter rather than a write-side one. The head reader
takes the tail of that partition unfiltered, which puts it on the relational side
of a split ADR 0047 records from the other:

| Engine     | Head | Spans PM | Criterion it serves |
|------------|------|----------|---------------------|
| PostgreSQL | MAX(global_position), unfiltered | yes | truthful tail of the log |
| KurrentDB  | $all through the aggregate-feed filter | no | the checkpoints' own view |
| DynamoDB   | log partition tail, unfiltered | yes | truthful tail of the log |

Both criteria are defensible and they disagree only when the log is PM-tailed.
Checkpoints advance off ReadAllAsync, which skips PM rows, so an unfiltered head
reports a position no projection can reach and a caught-up projection shows a
small permanent lag until the next aggregate append. KurrentDB avoids that;
PostgreSQL accepts it. DynamoDB joins PostgreSQL on substrate cost rather than
preference: no key separates the two families here, so a filtered tail means a
FilterExpression applied after the read, and a backwards Limit 1 Query then
returns an empty page whenever the tail is a PM row and must page backwards until
it finds an aggregate one. On a PM-heavy tail that walks an unbounded number of
rows on the hottest partition every time an operator loads the dashboard.
KurrentDB's filter is free because its engine applies it server-side before
paging; this engine would charge for it. The divergence is now pinned from both
sides: a PM append moves the DynamoDB head and does not move the KurrentDB one.

**Streams dispatch: wake-then-drain.** A stream record is a wake signal and
nothing more. The loop never parses one; it reads ReadAllAsync from the stored
checkpoint and dispatches what the log says, in position order. KurrentDB's
subscription can dispatch its record directly because $all is the ordered log.
DynamoDB Streams is not: it is a per-shard feed over table mutations, ordered
within a shard and unordered across shards, and one append lands rows under
several partition keys. Dispatching records directly would deliver an append's
rows in shard order rather than position order and interleave the counter's own
updates with events. The checkpoint is the truth and the stream is the trigger, so
sequence numbers are never state: nothing durable keys on them, and the iterators
are reacquired on restart. The table's stream is provisioned KEYS_ONLY, because an
image would be carried across the wire and dropped. Startup acquires its shard
iterators before it drains, which closes the window where an event committed
between the drain and the acquire would be missed by both. A drain is forced on
that first pass rather than waited for, so a quiet stream with a backlog still
catches up; after that a quiet stream costs no read of the event feed, which is
the PLAN.md's Phase 14 no-polling done-when reading: there is no interval polling of the event table, and the
native feed is the trigger. Dispatch is at-least-once like every other path here,
and a fault restarts the loop, which drains from the checkpoint again: redelivery
absorbed by per-handler idempotency, and degraded-mode availability rather than a
stall.

**Configuration: flat keys, and no DSN form.** DynamoDB is addressed by a service
URL and a table, so EVENT_STORE_DYNAMODB_SERVICE_URL and
EVENT_STORE_DYNAMODB_TABLE_NAME carry it and there is no connection string at all.
The provider-selection seam therefore validates provider configuration,
DSN-shaped or not: its DynamoDb arm checks those two keys rather than parsing a
string that does not exist, using Uri.TryCreate with an http or https scheme
check, because a UriFormatException is a FormatException that escapes both the
seam's own filter and the Workers host's configuration handler. The Api and
Workers hosts read the provider first and demand EVENT_STORE_CONNECTION_STRING
only for the engines that have one; the AdminConsole followed when it gained an
arm. The absence is the guard rather than a courtesy: demand a key the engine
never reads and an operator fills it with the DSN they are migrating off, and a
dropped provider key then lands on the parser's Postgres default and composes the
old engine against a live database. Credentials stay with the SDK's own chain, so
no key here can be mistaken for a place to put a secret and a deployed host
resolves them from its role. The cost is named: a host with no resolvable
credentials boots green and fails at the first append, the one address fault in
this design that is not a boot failure.

**The LocalStack pin, the package split, and the test-reach ledger.** LocalStack
is pinned to 4.14.0 and the pin is load-bearing rather than hygiene: the latest
tag exits 55 on startup with "License activation failed" unless an auth token is
set, and the tag list carries a community-archive tag, so the community edition is
archived rather than superseded. 4.14.0 is the last tag that starts with no token.
The Streams consumer API lives in AWSSDK.DynamoDBStreams, a package separate from
AWSSDK.DynamoDBv2, because the table client cannot read the stream it enables, so
the adapter carries both; the two namespaces collide on type names, which the
Streams dispatch tests pay for in fully-qualified references. Three test-reach
widenings are on the record, each labelled where it sits (TDD_RULES section 3):
MaxAppendAttempts is settable because 256 real losses is a load test rather than a
fact; CancellationVerdict is public because xUnit needs a public test class and a
public method cannot take an internal parameter; and the dispatcher's wake facts
decorate the injected clients rather than reaching inside, because the harness
already hands both to the service by hand. The one stand-in, a derived client that
cancels forever, exists only for the retry loop's exhaustion, which the live engine
cannot deterministically produce.

## Consequences

- The switching guarantee at PLAN.md's Phase 2 provider-switch done-when now extends to four engines. The Api,
  Workers, and AdminConsole hosts each compose a DynamoDb arm, and the provider
  value carries the engine through the write path, the read-side ports, and the
  projection feed.
- ADR 0004's revisit trigger fired during this arc rather than at its end: the
  fourth adapter would have been the fourth copy of the registry and JSON seam, so
  the seam collapsed into Infrastructure/Versioning first (ADR 0048) and this
  adapter was written against it.
- Operators on DynamoDB see a small permanent projection lag whenever the log is
  PM-tailed, and an append that fights for a position past 256 attempts fails
  loudly rather than blocking. Both are the ordering guarantee's price on this
  engine, and both are visible rather than silent.
- The head divergence is two defensible answers to one question rather than a
  defect, and it is now pinned from both sides: the KurrentDB and DynamoDB
  head-reader facts assert opposite PM behavior on purpose.
- Real AWS is untested. Every fact here is LocalStack's answer, and the emulator's
  synchronous GSI propagation is a demonstrated fidelity gap on the axis this
  design turns on, so the substrate choice rests on the API's refusal rather than
  on the emulator's behavior.

## Trigger for revisiting

A deployment needing more write throughput than one counter row serializes
reopens the ordering guarantee, not this cap. Real AWS, which PLAN.md's Phase 14
out-of-scope list excludes, reopens every fidelity claim here and the credential chain's boot
posture with it. An engine-native correlation index, or a dedicated correlation
projection, would reopen the tracer's unavailable state and be its own ADR. A
filtered head becomes affordable if the table ever grows a key that separates the
two families, which would close the ADR 0047 divergence rather than pick a side.
