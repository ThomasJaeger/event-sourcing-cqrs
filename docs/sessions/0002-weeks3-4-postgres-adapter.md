# Session 0002: Weeks 3-4 PostgreSQL adapter, Session 1 of 3

Date: 2026-05-11
Phase: 2 (Weeks 3-4) per PLAN.md; Session 1 of 3 in the reconciled six-week plan's PostgreSQL slice.

Scope: the first migration file (`0001_initial_event_store.sql`), the `EventStore.Postgres` project shell with `MigrationRunner`, and the `EventStore.Postgres.Cli` console host that invokes the runner. No `IEventStore` adapter, no `OutboxProcessor`, no append-and-dispatch integration tests. Those land in Sessions 0003 and 0004.

## Deliverables in this session

- `migrations/0001_initial_event_store.sql` creates the `event_store` schema, the `events`, `outbox`, `outbox_quarantine`, and `schema_migrations` tables, and the indexes the OutboxProcessor and AdminConsole correlation tracer will rely on.
- `src/Infrastructure/EventStore.Postgres/`: `MigrationRunner`, `MigrationRunnerOptions`, `MigrationChecksumMismatchException`. Embedded-resource loading from `/migrations/*.sql` via `LogicalName="EventStore.Postgres.Migrations.%(FileName)%(Extension)"`.
- `src/Infrastructure/EventStore.Postgres.Cli/`: single-file `Program.cs` parsing `migrate` and optional `--dry-run`, reading `EVENT_STORE_CONNECTION_STRING`, invoking the runner.
- `.gitattributes` at repo root: `* text=auto eol=lf` plus binary carve-outs. The migration runner's SHA-256 checksum on raw embedded bytes only stays reproducible across Windows and Linux/macOS checkouts when line endings are normalized at the Git layer.
- Both projects added to `EventSourcingCqrs.slnx`. `Npgsql` added to `Directory.Packages.props` at version 9.0.3.

## Schema decisions

PostgreSQL 16 syntax exclusive. IDENTITY columns, STORED generated columns, JSONB.

**Events.** `global_position` BIGINT IDENTITY primary key; `(stream_id, stream_version)` and `event_id` UNIQUE constraints back the only indexes the events table needs. Two STORED generated columns project `correlation_id` and `causation_id` out of `metadata` for the AdminConsole correlation tracer. The `metadata->>` cast to UUID fails loudly at INSERT on malformed metadata, which is the desired contract. Single non-constraint index `ix_events_correlation_id` on the generated correlation column.

**Outbox.** `outbox_id` BIGINT IDENTITY primary key; `event_id` UNIQUE. One STORED generated column (`correlation_id`), no `causation_id`: causation tracing is a stream-level concern on the events table, not a dispatch-level concern on the transient outbox envelope. Partial index `ix_outbox_pending ON event_store.outbox (outbox_id) WHERE sent_utc IS NULL` orders pending rows in FIFO outbox_id sequence for the OutboxProcessor's drain loop.

**Outbox quarantine.** Separate table, not a flag on the outbox row, so the live outbox stays small and the partial index stays cheap. No FK from `outbox_quarantine.outbox_id` to `outbox.outbox_id`: the OutboxProcessor's move-to-quarantine path deletes the live outbox row, so an FK would block the move or force `ON DELETE SET NULL` and lose the historical reference. `outbox_id` stays on the quarantine row as a historical reference only.

**Schema migrations.** Lives in the `event_store` schema alongside the application tables; created by migration 0001 itself inside the migration's own transaction. The runner detects a fresh database via `to_regclass('event_store.schema_migrations')` returning NULL, treats the applied set as empty, and lets migration 0001 create the table and insert its own row in the same transaction.

**Constraint naming.** `pk_<table>`, `uq_<table>_<column-or-tuple>`, `ix_<table>_<column-or-purpose>`. The `event_store.` schema prefix carries the "event store outbox" context, so outbox objects use `pk_outbox`, `uq_outbox_event_id`, `ix_outbox_pending`, `ix_outbox_correlation` rather than redundant `event_outbox_*` names.

## MigrationRunner decisions

**Public surface.** One method, `RunPendingAsync(MigrationRunnerOptions, CancellationToken)`. Options carry `ConnectionString`, `DryRun`, and an optional `Action<string> Log` sink. `Action<string>` rather than `ILogger` because no host exists yet to inject the logger; the CLI passes `Console.WriteLine` and the Session 1 tests pass a list-capturing delegate. The host-logging integration arrives when a host's `Program.cs` exists.

**Advisory lock key.** `MigrationAdvisoryLockKey = 0x4553_5243_515F_4D52L`, decimal `4995351488196677458`. The eight bytes read left-to-right as ASCII `E S R C Q _ M R`. A second runner targeting the same PostgreSQL instance must pick a different value to avoid collision on `pg_locks.objid`.

**Lock-across-batch behavior.** The runner acquires `pg_advisory_lock(MigrationAdvisoryLockKey)` once at the start of `RunPendingAsync` and releases it once at the end, holding it across all per-migration transactions, not inside any of them. If a migration in the middle of a batch fails and the runner exits, the lock releases on connection close. On the operator's re-run, the same lock blocks any concurrent runner from racing into a database that is only partially migrated; the runner reads `schema_migrations`, sees the partial batch, and applies the remainder. The lock is the gate that keeps the system either on a known-good migration set or fully recoverable by re-running.

**Checksums.** SHA-256 over the raw embedded resource bytes, hex-encoded lowercase. Hashing bytes (not a decoded-and-reencoded string) avoids BOM and encoding round-trip drift. The `.gitattributes` rule normalizes `.sql` line endings to LF on every platform, keeping checksums reproducible across developer machines.

**Embedded resources.** `/migrations/*.sql` linked into the assembly via the EventStore.Postgres csproj, exposed under the `EventStore.Postgres.Migrations.` resource prefix. Single on-disk source of truth, single embedded copy in the assembly. `LoadEmbeddedMigrations` parses the `NNNN_description.sql` pattern, sorts by version, and rejects duplicate version numbers at load time so the failure surface is a clear runner error rather than a PG primary-key violation on the second `INSERT INTO schema_migrations`.

**Dry-run.** Same code path minus the lock and the apply step. Still reads `schema_migrations`, still verifies checksums of already-applied migrations, still throws `MigrationChecksumMismatchException` on a mismatch. A mismatch is information the operator wants whether or not they intended to apply pending work; the dry-run and the normal run share the failure mode.

**Bootstrap of `schema_migrations`.** The table is created by migration 0001 itself, inside the same transaction that inserts its own application row. On a fresh database `to_regclass('event_store.schema_migrations')` returns NULL, the runner treats the applied set as empty, and migration 0001 creates the table and inserts the row in one transaction. No separate `EnsureMigrationsTableAsync` step duplicates the DDL outside the migration file.

**CLI.** Small `EventStore.Postgres.Cli` console project, single `Program.cs`. The planning instruction allowed either extending an existing host's `Program.cs` or adding a small console project. No existing host takes arguments yet, so the console project ships first. Embedded-startup integration (host's `Program.cs` calling `MigrationRunner.RunPendingAsync` before binding ports) arrives in Session 0004 or later. Exit codes: 0 on success, 1 on runner failure, 64 (EX_USAGE) on usage error, 78 (EX_CONFIG) on missing `EVENT_STORE_CONNECTION_STRING`.

## Track A flags

Source: planning conversation for this session. Captured here with discovery context so the manuscript track can act on them without re-deriving.

1. **Figure 8.2 declares `IX_Events_Stream` alongside the `(StreamId, Version)` UNIQUE constraint.** The UNIQUE constraint already backs an index; the named index is redundant. Implementation omits it.
2. **Figure 8.2 shows `VARBINARY(MAX)` for `Payload` and `Metadata`.** The Event Metadata section's prose says JSONB. Implementation uses JSONB; the figure's column type needs updating to match the prose.
3. **`GlobalSequence` vs `GlobalPosition` naming and envelope-vs-metadata placement inconsistency** across Chapters 8, 13, 14, 16, and Part 4. Implementation commits to `envelope.GlobalPosition` when the projection-facing API lands in Weeks 5-6; schema column is `global_position`.
4. **Chapter 8's "Choosing an Event Store" section names Marten as a peer** alongside PostgreSQL, KurrentDB, DynamoDB. v1 implements three peers, not four; the sentence needs editing so Marten reads as an alternative the reader could swap in rather than a shipped adapter.
5. **Chapter 17's investigation SQL uses `ORDER BY OccurredUtc`.** `ORDER BY global_position` is stricter (monotonic, never tied) and matches how the implementation orders the events table. Optional polish.
6. **Figure 8.4 omits `EventType` from the Outbox table** despite the OutboxProcessor pseudocode logging `msg.EventType`. Implementation includes `event_type`.
7. **Figure 8.4 names the timestamp column `CreatedUtc`.** Chapter 17's outbox-depth metric uses `occurred_at`. Implementation uses `occurred_utc` to match the events table and the surrounding prose; figure column name needs updating.
8. **Figure 8.4 has no `LastError` or backoff column** despite the surrounding prose committing to both. Implementation includes `last_error` and `next_attempt_at`.
9. **Figure 8.4's partial index is `ON Outbox(SentUtc) WHERE SentUtc IS NULL`.** The indexed column is always NULL within the filter. Implementation uses `ON event_store.outbox (outbox_id) WHERE sent_utc IS NULL` so the index orders pending rows in FIFO sequence.
10. **Chapter 8 does not mention migration runners** or the operational concern of applying schemas to the event store. The reference implementation uses hand-rolled SQL files plus a small Npgsql-based runner. One paragraph near Figure 8.2 would close the gap and make the schema feel applyable rather than only declarable.

## Notes for the next sessions

**Session 0003 (PostgreSQL `IEventStore` adapter).** Adds the `EventStore.Postgres` project's IEventStore implementation against Npgsql: `AppendAsync`, `ReadStreamAsync`, atomic events-plus-outbox writes in one transaction, optimistic concurrency mapped to the `uq_events_stream_version` violation, JSON serialization with type-name resolution. The runner shipped in this session does not block the adapter's development; both can land in parallel commits.

**Session 0004 (OutboxProcessor).** Drains the outbox to the in-process bus, with backoff via `next_attempt_at`, error capture in `last_error`, and move-to-quarantine for poison messages. **Reminder, from this session's schema design:** `outbox_quarantine.attempt_count` is NOT NULL with no default. The quarantine path must read the live outbox row's `attempt_count` and copy it into the quarantine INSERT. Trivial in code, easy to forget without this note.

**Embedded-startup migration call.** Once a host's `Program.cs` exists (likely Workers or Api in a later phase), it should call `MigrationRunner.RunPendingAsync` before binding ports, so `docker compose up` brings the host up and migrates the database in one step. The CLI shipped in this session is the operations path; the embedded call is the developer-experience path.
