# 0017. Timeouts via a PostgreSQL Delay Queue

## Status

Accepted (May 2026)

## Context

A process manager waits for events that may never arrive. OrderFulfillment
authorizes a payment and waits for PaymentAuthorized; if the provider never
responds, the workflow hangs forever. A timeout is how a workflow says "wait
for this event, but if it has not arrived within X, take a fallback path." The
delay queue is the infrastructure that makes that a reliable primitive: schedule
a command to dispatch at a future time, cancel it if the awaited event arrives
first, and let it fire if it does not.

Phase 5's process managers need this for the awaiting states where the PM rests
for an asynchronous event that may never arrive. Nothing built so far dispatches
future work: the command bus dispatches now, and the outbox dispatches a row as
soon as it is visible. A timeout is the first piece of infrastructure that
dispatches later.

The mechanism is a PostgreSQL table plus a polling background service, the same
shape the outbox already uses (Sessions 0004 and 0006). The reference
implementation runs Postgres, polls it for the outbox, and wakes on
LISTEN/NOTIFY; a delay-queue table fits that operational model with no new kind
of moving part. Distributed schedulers and broker-native delay features are out
of scope, consistent with the in-process-bus-driven-by-Postgres choice the rest
of the system makes.

## Decision

### The table

```sql
CREATE TABLE event_store.delayed_commands (
    delayed_command_id     BIGINT       GENERATED ALWAYS AS IDENTITY,
    fire_at_utc            TIMESTAMPTZ  NOT NULL,
    command_type           TEXT         NOT NULL,
    command_payload        JSONB        NOT NULL,
    correlation_id         UUID         NOT NULL,
    causation_id           UUID         NOT NULL,
    actor_id               UUID         NOT NULL,
    service_name           TEXT         NOT NULL,
    idempotency_key        TEXT         NOT NULL,
    scheduled_by_stream_id TEXT         NOT NULL,
    scheduled_by_step      TEXT         NOT NULL,
    scheduled_utc          TIMESTAMPTZ  NOT NULL DEFAULT now(),
    dispatched_at_utc      TIMESTAMPTZ  NULL,
    cancelled_at_utc       TIMESTAMPTZ  NULL,
    cancellation_reason    TEXT         NULL,
    attempt_count          INT          NOT NULL DEFAULT 0,
    last_error             TEXT         NULL,
    next_attempt_at        TIMESTAMPTZ  NULL,
    CONSTRAINT pk_delayed_commands PRIMARY KEY (delayed_command_id)
);

CREATE INDEX ix_delayed_commands_due
    ON event_store.delayed_commands (fire_at_utc)
    WHERE dispatched_at_utc IS NULL AND cancelled_at_utc IS NULL;

CREATE INDEX ix_delayed_commands_by_stream
    ON event_store.delayed_commands (scheduled_by_stream_id, scheduled_by_step)
    WHERE dispatched_at_utc IS NULL AND cancelled_at_utc IS NULL;
```

A companion `event_store.delayed_commands_quarantine` table holds rows whose
dispatch exhausted its retries, mirroring `outbox_quarantine`: a terminal
table reached by an atomic CTE move so the live table stays small and its
partial indexes stay cheap. Constraint naming follows migration 0001's
`pk_`/`uq_` convention.

`command_type` plus `command_payload` store the command as JSON, because a
delayed command is dispatched in a different process moment than the one that
scheduled it. The next block of columns (`correlation_id`, `causation_id`,
`actor_id`, `service_name`, `idempotency_key`) are exactly the values
`CausedCommandBus` needs to rebuild a command context (ADR 0014), so the row is
self-describing and the processor dispatches without a lookup. `actor_id` plus
`service_name` are the two fields of the ADR 0014 `SystemActor`, not a single
opaque string. `scheduled_by_stream_id` plus `scheduled_by_step` identify which
process-manager step scheduled the row, which is what `CancelAsync` matches on.

`attempt_count` and `next_attempt_at` look related but serve different jobs:
`attempt_count` is checked against the retry limit to decide quarantine, and
`next_attempt_at` is the timestamp the due query filters on to decide when a
failed row may be retried. A row can have a high `attempt_count` and a
`next_attempt_at` far in the future at the same time.

The two indexes are partial on `WHERE dispatched_at_utc IS NULL AND
cancelled_at_utc IS NULL`. An unbounded delay queue accumulates dispatched and
cancelled rows that no due-row or cancellation query cares about; the partial
predicate keeps each index sized to active work rather than total history. The
outbox's `ix_outbox_pending` uses the same pattern.

### The IDelayQueue port

```csharp
public interface IDelayQueue
{
    Task ScheduleAsync(
        ICommand command,
        DateTimeOffset fireAtUtc,
        StreamId scheduledByStream,
        string scheduledByStep,
        EventMetadata causingEventMetadata,
        SystemActor actor,
        string idempotencyKey,
        CancellationToken ct);

    Task<bool> CancelAsync(
        StreamId scheduledByStream,
        string scheduledByStep,
        string cancellationReason,
        CancellationToken ct);
}
```

`ScheduleAsync` carries the full dispatch metadata so the row is self-describing.
It takes `ICommand` rather than a generic `TCommand`, matching `ICausedCommandBus`.
From `causingEventMetadata` it stores `correlation_id` (the event's CorrelationId)
and `causation_id` (the event's EventId), so the timeout command's causation
points back through the event that prompted the process manager to set the
timeout, keeping the Phase 9 Correlation-ID Tracer chain intact. `CancelAsync`
matches on the scheduling stream and step, and returns whether any pending row
was cancelled so a caller can log "no active timeout to cancel" without the
information being load-bearing.

### Claim mechanism: row lock, not a claim column

The processor selects due rows with `FOR UPDATE SKIP LOCKED` and dispatches
inside the same transaction that holds the lock, exactly as the outbox does. The
row lock is the claim; there is no `claimed_at`/`claimed_by` column and no
recovery sweeper. On crash, Postgres releases the lock and the row reverts to
due with no cleanup code.

This works because a timeout command carries an idempotency key, so at-least-once
delivery is made effectively-once by the consumer. The outbox needs no claim
column because projection handlers are idempotent by construction; the delay
queue needs no claim column because timeout commands are idempotent by ADR 0016.
Same root mechanism (idempotency at the consumer), same simplification. A delay
queue designed before the idempotency mechanism existed would reach for claim
columns and a sweeper to approximate exactly-once; with idempotency in place,
that machinery is redundant.

The cost is that the transaction stays open while the command dispatches.
`SKIP LOCKED` means other due rows keep processing; only the in-flight row is
locked. The dispatched work (a timeout that triggers compensation) is the same
kind of work the outbox already runs inside its transaction once it dispatches
to process-manager handlers, so accepting it here keeps the two queues
consistent rather than optimizing one and not the other.

### Dispatch flow

`DelayQueueProcessor` reconstructs the dispatch context from the row's columns
(an `EventMetadata` from `correlation_id` and `causation_id`, a `SystemActor`
from `actor_id` and `service_name`) and dispatches through `ICausedCommandBus`.
A timeout command therefore flows through the same dispatch path as any command
a process manager originates: causation propagated through `CausedCommandBus`
(ADR 0014, ADR 0015), and deduplication through `IdempotencyBehavior` (ADR 0016)
on the row's idempotency key. The delay queue adds no dispatch infrastructure of
its own.

Deserializing `command_payload` back to a concrete `ICommand` needs the
`command_type` resolved to a CLR type, which requires a command-type registry
paralleling `EventTypeRegistry`. That registry lands in commit 16 alongside the
`PostgresDelayQueue` adapter.

### Typed timeout commands

Each timeout kind is its own command type, each with its own `ICommandHandler`
that loads the process manager, guards on the awaited state, and routes into the
shared compensation. Two ship: `TimeoutAwaitingPaymentForOrder` and
`TimeoutAwaitingDispatchForOrder`. The alternative, one generic timeout command
with a kind discriminator and a routing dispatcher, costs one class instead of N
but reintroduces magic-string routing the typed command bus exists to avoid. The
typed cost is bounded by the timeout-kind count and the type-safety benefit scales
with the codebase's lifetime, so typed is the clearer demonstration for a
reference implementation.

This Decision originally named four timeout commands, one per awaiting state. The
synchronous reservation fan-out the OrderFulfillment PM shipped (Decision 10)
makes `AwaitingInventory` and `AwaitingShipment` transient: the PM passes through
them inside one `PaymentAuthorized` handler invocation rather than resting there
for an external event. Only `AwaitingPayment` (awaiting `PaymentAuthorized`) and
`AwaitingDispatch` (awaiting `ShipmentDispatched`) are genuine wait states a
timeout protects, so the set is two, not four. A timeout for a state the PM never
rests in would be dead infrastructure.

### Cancellation: active by default, state guard as the safety net

A process manager calls `CancelAsync` when it transitions past a
timeout-scheduling state (the awaited event arrived). This is the operational
story: an active cancellation keeps the dashboard clean, with no zombie rows
showing as due for a workflow that already moved on. The lazy state guard is the
correctness story: if a process manager crashes between the transition and the
cancel call, the timeout still fires, but its handler re-checks the
process-manager state and no-ops because the workflow already advanced. The two
are not redundant; they cover different failure modes. Active cancellation
handles the normal path cleanly; the state guard makes the design correct when
the cancel call never runs.

### Wake mechanism

An `AFTER INSERT` trigger on `delayed_commands` fires `pg_notify` on every
insert, mirroring migration 0005's outbox trigger, and the processor polls the
due index on a fallback timer. The trigger exists and fires the same way the
outbox's does; what differs is how much of the load it carries. The outbox's
rows are due the moment they are inserted, so its insert notification is the
main wake signal. The delay queue's rows are due at a future `fire_at_utc`, so a
wake on a future-dated insert finds nothing due and returns to the timer, and
the timer is what eventually fires those rows. The notification mainly shortens
latency for rows scheduled at or near the present (an immediate retry, a near-due
timeout). The notification is an optimization on top of the timer, not the
primary trigger the way it is for the outbox.

## Consequences

- A new `DelayQueueProcessor` background service runs in the Workers host,
  parallel to `OutboxProcessor` and landing in commit 17. It polls the due
  index, dispatches through `ICausedCommandBus`, and applies the same
  full-jitter exponential backoff (base 1s, cap 5min) and quarantine-after-N
  policy the outbox uses; whether it reuses `OutboxRetryPolicy` or a renamed
  shared policy is a commit-17 implementation choice.
- `IDelayQueue` is a new port in Domain.Abstractions; `PostgresDelayQueue`
  implements it in EventStore.Postgres alongside the other Postgres adapters,
  consuming the same `INpgsqlConnectionFactory`. The schema migration and the
  command-type registry land in commit 16; the processor lands in commit 17.
- The delay queue and the outbox are deliberately the same operational shape:
  both run in the Workers host, both poll Postgres, both wake on LISTEN/NOTIFY,
  both retry with backoff and quarantine, both claim with `FOR UPDATE SKIP
  LOCKED`. The similarity is intentional consistency, not missed consolidation.
  The distinction is load-bearing: the outbox dispatches events to event
  handlers, the delay queue dispatches commands to command handlers; the two
  have different dispatch surfaces, and merging them would couple event delivery
  to command scheduling for no gain.
- A second LISTEN/NOTIFY trigger on `delayed_commands` inserts mirrors migration
  0005's trigger on the outbox. The duplication is deliberate, the same trigger
  pattern for the same wake need.
- The row-lock claim depends on consumer idempotency for its at-least-once
  safety. `IdempotencyBehavior` is the consumer idempotency for command dispatch
  the way idempotent projection handlers are for event dispatch. If a future
  timeout command were dispatched without an idempotency key, the row-lock claim
  would no longer be safe for it; the `ScheduleAsync` contract requires a key for
  exactly this reason.

## Trigger for revisiting

- The KurrentDB adapter (Phase 10) and the DynamoDB adapter (Phase 11) will want
  different delay mechanisms: KurrentDB has native scheduled messages, and
  DynamoDB pairs TTL expiry with Streams. Both are expected adapter-shape
  changes behind `IDelayQueue`, not events that supersede this ADR, the same way
  ADR 0011 anticipated per-adapter stream-id handling.
- Heavy or long-running timeout dispatch makes the open-transaction-during-
  dispatch cost (held connection, delayed vacuum horizon) outweigh the
  simplicity of the row-lock claim. The claim mechanism would move to
  claim-via-column with dispatch outside the transaction and a recovery sweeper,
  at the cost of the divergence from the outbox.
- The timeout-kind count grows large enough that N typed commands plus N
  handlers becomes a maintenance burden. The generic-command-with-discriminator
  alternative is reconsidered, trading type safety for fewer classes.

## Amendment (Phase 10, multi-tenancy)

Migration 0020 adds a `tenant_id` discriminator to `delayed_commands` and
`delayed_commands_quarantine` under the shared-schema isolation model. The column
is flat and keeps its NOT NULL DEFAULT, the read-model add-with-default idiom
rather than the drop-default posture migration 0019 took for the command
idempotency table. The posture differs because the schedule write is an
off-command-path worker write that always names the tenant from the causing
metadata, so the default is the honest fallback for a pre-existing or
unnamed-tenant row, not a fail-closed backstop. The command-idempotency table sits
on the command path, where a missing tenant is a dispatch-wiring regression, so it
drops its default to raise; the delay queue does not.

`ScheduleAsync` stamps `tenant_id` from `causingEventMetadata.Tenant`. The
processor selects the column into the due row and rebuilds the dispatch metadata
with `Tenant: row.Tenant`, so a resurfaced command dispatches under the tenant
that scheduled it rather than the default. The row is self-describing in its
tenant the way it already is in its correlation and causation: the column the
scheduler wrote is the tenant the dispatch reconstructs. The quarantine move
carries `tenant_id` through its atomic CTE, so an operator diagnosing a
quarantined row sees which tenant's command exhausted its retries.

A known consequence, recorded rather than hidden: the OrderFulfillment
process-manager stream stays at the default-tenant form. The timeout handler loads
the PM through `OrderFulfillmentStreams.For`, which composes the stream id under
the default tenant, and the in-process process-manager dispatch loop does not set
the current-tenant accessor, so the PM read and write stay default-formed. The
tenant the resurfaced timeout carries still reaches the tenant-partitioned Order
aggregate at `order:{tenant}:{id}`: the caused bus sets the current-tenant
accessor from the dispatched metadata's tenant, and the aggregate repository
resolves the aggregate stream from that accessor, so the compensation's
`CancelOrder` lands on the scheduling tenant's order. Tenant-qualifying the
process-manager stream itself is the separate process-manager-propagation slice's
work, and the per-tenant replay coherence of that stream is P10.9's concern.

The test reach is stated honestly. The deterministic harness proves the persisted
tenant on the scheduled row and the tenant on the rebuilt dispatch metadata. No
single deterministic test drives a non-default-tenant timeout all the way to a
cancelled non-default-tenant order: the only harness that wires that full chain is
the live Workers-host path already recorded as a timing race, and the remaining
links, the caused-bus dispatch onto the Order aggregate through the
tenant-resolving repository, are pinned by their own tests. P10.9's structural
cross-tenant extension to the command and delay-queue boundaries is the durable
home for closing that end-to-end gap.

## Amendment (Phase 10, self-cancel under the row-lock claim)

The "Cancellation: active by default" path and the "Claim mechanism: row lock"
path interact when the cancelled timeout is the firing timeout. A process manager
that reaches a timeout-scheduling state schedules the row under its own stream and
step; when that timeout later fires, its handler runs the compensation, and the
compensation's first effect cancels the timeout for that same stream and step. The
firing row is the row the processor holds under FOR UPDATE while it dispatches. The
original CancelAsync was a plain UPDATE on a separate connection, so it blocked on
the dispatcher's row lock while the dispatching transaction waited on the cancel: a
self-lock the database cannot detect, because one side waits on an in-process await
rather than a database lock.

CancelAsync now selects its target rows with FOR UPDATE SKIP LOCKED and updates only
those, so a row another transaction holds locked is skipped. A locked row is
mid-dispatch, and a firing timeout must be delivered, not cancelled, so skipping it
is the correct behavior: the zero-row result reports nothing to cancel because the
timeout is already being delivered. A future-dated row sitting idle is unlocked, so
the normal cancel-on-transition path (the awaited event arrived first) still cancels
it. The change is to the cancellation query alone; the row-lock claim, the dispatch
flow, and the state guard are unchanged. The original Decision's cancellation
narrative holds for the idle future-dated row it described; this amendment records
the firing-row case the active-cancellation default did not account for.
