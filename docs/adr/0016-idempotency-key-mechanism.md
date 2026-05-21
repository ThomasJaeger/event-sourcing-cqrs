# 0016. Idempotency-Key Mechanism

## Status

Accepted (May 2026)

## Context

Commands retry. The outbox redelivers a triggering event when a process-manager
handler throws and is re-run (ADR 0015); a timeout fires a command that may
duplicate one already dispatched (ADR 0017, forthcoming); a Phase 7 UI
resubmits a command when a user double-clicks or a client retries a timed-out
request. Without deduplication, a retried command applies its effect twice: two
payment authorizations, or two inventory reservations against the same line.

ICommandContext already carries a nullable IdempotencyKey (ADR 0014). The
CausedCommandBus seam populates it for process-manager dispatch, and the
user-dispatch path leaves it null. What is missing is the mechanism that reads
the key and skips a command already processed.

Three placement options. Aggregate-side dedup scans the aggregate's own event
stream for a marker before applying, which couples idempotency awareness into
every aggregate and turns long streams into expensive scans. A sender-side
wrapper around the bus (Chapter 10's IdempotentCommandSender) works but sits
outside the pipeline, so it does not compose with the logging and validation
behaviors already there. A pipeline behavior is the project's standard mechanism
for a cross-cutting concern and is where logging and validation already live.

## Decision

A pipeline behavior, IdempotencyBehavior, consults a dedicated table before
dispatching to the handler:

```sql
CREATE TABLE event_store.command_idempotency (
    idempotency_key TEXT NOT NULL PRIMARY KEY,
    command_type    TEXT NOT NULL,
    processed_utc   TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ix_command_idempotency_processed_utc
    ON event_store.command_idempotency (processed_utc);
```

A command with a null key skips the behavior: deduplication is opt-in by the
originator supplying a key. A command with a key runs the
eager-check-with-lazy-fallback pattern. The behavior reads the table for the key
first. A hit means the command was already processed, and the behavior returns
without dispatching. A miss dispatches the handler and then records the key. The
primary-key constraint is the lazy fallback: if two dispatches with the same key
both miss the eager check and race, the second insert violates the constraint,
and the behavior reads that violation as a duplicate. Eager alone has the race;
lazy alone pays a constraint-violation round-trip on every legitimate first
write; the hybrid does the cheap read first and falls back to the constraint
only when the race actually fires.

Keys are supplied by the originator, never derived from the command payload. A
hash of the payload silently merges operations that are legitimately distinct
but happen to carry identical fields: two 50-dollar refunds to the same customer
on the same day are two refunds, not one retried refund, and a payload hash
cannot tell them apart. The originator knows what the payload cannot encode,
which is whether two dispatches are the same logical operation.

Two originator patterns:

- Process-manager commands derive a deterministic key from the PM identity and
  the workflow step, so two redeliveries of the same triggering event compute
  the same key and the second sees the first's row. The format is the PM stream
  id plus the step plus an optional sub-id: `{pm-stream-id}:{step}[:{sub-id:N}]`,
  for example `pm-order-fulfillment:7b8c...:authorize-payment`, or
  `pm-order-fulfillment:7b8c...:reserve:a1b2...` for a per-line fan-out.
  Derivation lives in a static helper,
  IdempotencyKeys.ForProcessManager(StreamId processManagerStream, string step,
  Guid? subId = null), in Domain.Abstractions. A static helper rather than a
  method on the ProcessManager base because the key is a pure function of the
  stream id and the step, the delay queue computes the same key shape for
  timeout commands without a PM instance in hand (ADR 0017), and one function is
  the single place the format lives and a test pins it. The helper ships with
  its first consumer, not in this ADR's commit.
- UI and API commands (Phase 7+) carry a client-generated UUID minted at
  user-intent time and resubmitted unchanged across retries. ADR 0016 names
  this; the HTTP edge that reads and enforces it is Phase 7 work.

Most event-sourced commands return void. A duplicate of a void command returns
no-op success: the effect already happened, and there is nothing to return.
Commands that return data (Phase 7+) will need the response cached so a
duplicate returns the same data; that pattern is out of scope here and named in
the Trigger section.

## Consequences

- New IIdempotencyStore and PostgresIdempotencyStore plus the migration creating
  event_store.command_idempotency (commit 13), and IdempotencyBehavior
  registered in the pipeline (commit 14). This ADR commits the mechanism; the
  code lands in those commits.
- IdempotencyBehavior sits inside LoggingBehavior and before validation.
  LoggingBehavior stays outermost so its one-log-line-per-command guarantee
  covers duplicates as well as commands that short-circuit on validation
  failure, which keeps a duplicate flood visible as an operational signal;
  IdempotencyBehavior then short-circuits the duplicate before validation does
  any work and before the handler runs. Marking that log line as a duplicate is
  a commit-14 detail; this ADR commits the ordering and the short-circuit point.
- The command_idempotency columns: idempotency_key as primary key is what powers
  the lazy fallback. command_type is for operational tooling (which command kind
  a dedup decision applied to) and for the future cached-response pattern (the
  same key arriving with a different command type is a logic error worth
  flagging). processed_utc supports retention; the index on it makes time-based
  pruning or partitioning possible later. Retention itself is deferred; the
  column shape that enables it is committed now.
- The table is a front-line optimization, not the sole guarantee. Aggregate
  optimistic concurrency on (stream, version) remains the correctness backstop
  against double-apply if a race lets two handler runs both reach the event
  store.
- The delay queue (ADR 0017) writes an idempotency_key on each scheduled-command
  row and flows it into the command context at dispatch time, so a timeout
  command deduplicates through this same behavior without separate
  infrastructure.

## Trigger for revisiting

- A Phase 7+ command handler returns data and a duplicate must return the same
  response. The cached-response pattern (storing the response body keyed by
  idempotency_key) extends the table and the behavior; the command_type column
  and the key already anticipate it.
- A distributed topology partitions the event store across multiple databases,
  so a single command_idempotency table no longer sees every dispatch. Keys
  would need partitioning or a shared dedup store, and the single-table
  assumption here would not hold.
- A use case needs a duplicate dispatch to be visibly distinguishable from a
  first dispatch. The no-op-success semantic would no longer fit, and the
  behavior would need to signal "already processed" to the caller.
