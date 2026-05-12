# Session 0003: Weeks 3-4 PostgreSQL adapter, Session 2 of 3

Date: 2026-05-12
Phase: 2 (Weeks 3-5) per PLAN.md; Session 2 of 3 in the PostgreSQL-adapter slice. Phase 2 was extended to Weeks 3-5 in commit fb5da10 to make room for the SQL Server adapter as a parallel relational implementation later in the same phase.

Scope: the PostgreSQL `IEventStore` adapter (`PostgresEventStore`) with atomic events-plus-outbox writes inside a single `NpgsqlTransaction`, `EventTypeRegistry` for type-name resolution, optimistic-concurrency translation, and Testcontainers-backed integration tests. No `OutboxProcessor`, no SQL Server adapter, no real metadata in `EventStoreRepository`. Those land in Session 0004 and beyond.

## Commits

1. `a50931b` Collapse ConcurrencyException to two-argument shape (Session 0003 pre-step)
2. `c962cf0` Add EventTypeRegistry and PostgresFixture migrated-database helper
3. `f68eadf` Implement PostgresEventStore with atomic events-plus-outbox writes

The pre-step landed as a standalone commit so the `Domain.Abstractions` diff stays independent of the adapter work that builds on it.

## Deliverables in this session

- `src/Domain.Abstractions/ConcurrencyException.cs` collapsed to two arguments. `ActualVersion` removed; the caller's responsibility on concurrency conflict is to reload the stream and replay, at which point the actual version is implicit in the reload.
- `src/Infrastructure/EventStore.Postgres/EventTypeRegistry.cs` and `UnknownEventTypeException.cs`. Fluent registration, ordinal name comparison, loud failures on duplicate registration and on unknown lookups.
- `src/Infrastructure/EventStore.Postgres/PostgresEventStore.cs`. Constructor takes `NpgsqlDataSource`, `EventTypeRegistry`, `JsonSerializerOptions` from the composition root. `AppendAsync` writes events and outbox rows in one transaction. `ReadStreamAsync` orders by `stream_version` and rebuilds envelopes through the registry.
- `EventStore.Postgres.csproj` gains a `ProjectReference` to `Domain.Abstractions`. Session 0002's `MigrationRunner` and CLI did not need `IDomainEvent`, so the reference had never been wired up. Discovered at first build of `EventTypeRegistry`.
- `tests/Infrastructure.Tests/Postgres/PostgresFixture.cs` gains `CreateMigratedDatabaseAsync`. Adapter tests use it; migration-runner tests stay on the bare `CreateDatabaseAsync` path because they exercise the runner itself.
- `PostgresEventStoreTestKit.cs` (JSON options factory, registry factory, envelope builder), plus `EventTypeRegistryTests.cs`, `PostgresEventStore_AppendAsync_Tests.cs`, `PostgresEventStore_ReadStreamAsync_Tests.cs`.

## Decisions in this session

**Registry as authoritative for storage-side type names.** Both `event_store.events.event_type` and `event_store.outbox.event_type` are populated by `registry.NameFor(envelope.Payload.GetType())`. `EventEnvelope.EventType` is informational for in-memory paths and ignored by the adapter on writes. The registry catches the case where a caller constructs an envelope with one string but the runtime payload is a different CLR type; the envelope's string would pass that mismatch through silently. On reads the rebuilt envelope's `EventType` field is filled from the row's `event_type` column, so round-trip equality holds.

**Two-construction-site `UnknownEventTypeException`.** The registry constructs the one-argument form because it has no stream context at lookup time. The adapter's read path catches and rethrows the two-argument form with the stream ID and the original exception chained as `innerException`. Stack-trace continuity matters for debugging exception chains; two construction sites is the cost.

**`InvalidOperationException` for duplicate registration.** Configuration errors at startup do not have catch handlers; they crash the host. A typed `DuplicateEventTypeRegistrationException` would add a type that no caller will catch. The diagnostic content is in the message, not the type. The unknown-type case is different because a projection rebuilding from a stream with a deprecated event type might catch it; that warrants a typed exception.

**Filtered `23505` mapping.** Only `uq_events_stream_version` violations become `ConcurrencyException`. Violations on `uq_events_event_id` or `uq_outbox_event_id` propagate as the original `PostgresException` because a duplicate event ID is a programming bug or an idempotency failure, not a concurrency conflict the caller can recover from by reloading the stream. `Append_propagates_unique_violation_on_event_id_unchanged` is the regression test for this filter.

**Snake-case JSON keys validated end to end.** `JsonNamingPolicy.SnakeCaseLower` on the test kit's `JsonSerializerOptions` produces `correlation_id` and `causation_id` keys in stored metadata, which is what the schema's STORED generated columns extract on. The round-trip test asserts both the raw key shape (`SELECT payload::text`) and the populated generated column value, so a future drift in either direction surfaces at the assertion that fires first.

**Static parameter-binding helpers private to the adapter.** `AddUuid`, `AddText`, `AddInteger`, `AddSmallInt`, `AddJsonb`, `AddTimestampTz` remove visual noise from the SQL setup without hiding the column mapping. `AddTimestampTz` enforces `DateTimeKind.Utc` at the boundary; mis-Kinded DateTimes throw before reaching Npgsql.

**No explicit transaction rollback on the concurrency path.** The `await using` on `NpgsqlTransaction` rolls back automatically if the transaction is not committed by the time the scope exits. Adding an explicit `RollbackAsync` in the catch block would duplicate the dispose behavior without changing observable semantics.

## Track A flags

Source: Session 0002 carry-over (flags 1-10) plus Session 0003 planning conversation (flags 11-25). Captured with discovery context so the manuscript track can act on each without re-deriving.

### Carry-over from Session 0002 (figure-discrepancy reconciliation)

1. **Figure 8.2 declares `IX_Events_Stream` alongside the `(StreamId, Version)` UNIQUE constraint.** The UNIQUE constraint already backs an index; the named index is redundant. Implementation omits it.
2. **Figure 8.2 shows `VARBINARY(MAX)` for `Payload` and `Metadata`.** The Event Metadata section's prose says JSONB. Implementation uses JSONB; the figure's column type needs updating to match the prose.
3. **`GlobalSequence` vs `GlobalPosition` naming and envelope-vs-metadata placement inconsistency** across Chapters 8, 13, 14, 16, and Part 4. Implementation commits to `envelope.GlobalPosition` when the projection-facing API lands in Weeks 5-6; schema column is `global_position`.
4. **Chapter 8's "Choosing an Event Store" section names Marten as a peer** alongside PostgreSQL, KurrentDB, DynamoDB. The implementation commits to four hand-rolled peers (PostgreSQL, SQL Server, KurrentDB, DynamoDB); Marten reads as a swap-in alternative for the PostgreSQL slot rather than a shipped adapter.
5. **Chapter 17's investigation SQL uses `ORDER BY OccurredUtc`.** `ORDER BY global_position` is stricter (monotonic, never tied) and matches how the implementation orders the events table. Optional polish.
6. **Figure 8.4 omits `EventType` from the Outbox table** despite the OutboxProcessor pseudocode logging `msg.EventType`. Implementation includes `event_type`.
7. **Figure 8.4 names the timestamp column `CreatedUtc`.** Chapter 17's outbox-depth metric uses `occurred_at`. Implementation uses `occurred_utc` to match the events table and the surrounding prose; figure column name needs updating.
8. **Figure 8.4 has no `LastError` or backoff column** despite the surrounding prose committing to both. Implementation includes `last_error` and `next_attempt_at`.
9. **Figure 8.4's partial index is `ON Outbox(SentUtc) WHERE SentUtc IS NULL`.** The indexed column is always NULL within the filter. Implementation uses `ON event_store.outbox (outbox_id) WHERE sent_utc IS NULL` so the index orders pending rows in FIFO sequence.
10. **Chapter 8 does not mention migration runners** or the operational concern of applying schemas to the event store. The reference implementation uses hand-rolled SQL files plus a small Npgsql-based runner. One paragraph near Figure 8.2 would close the gap and make the schema feel applyable rather than only declarable.

### New in Session 0003 (planning conversation)

11. **Chapter 11's `DeserializeCurrent` resolver is referenced but never named or defined.** Implementation introduces `EventTypeRegistry` to fill this role. The registry currently lives in `Infrastructure/EventStore.Postgres` because the PostgreSQL adapter is the only consumer; it moves to `Infrastructure/Versioning` in Phase 12 alongside the upcaster pipeline.
12. **Chapter 11's `UpcastingDeserializer` takes a single `int currentVersion` in its constructor.** This doesn't generalize across multiple event types each at their own current version. Phase 12 concern.
13. **Chapter 11 introduces `IUpcasterFanOut` for splits but never shows the unified `Deserialize` signature** that returns `IEnumerable<IDomainEvent>` for both single-event and fan-out paths. Phase 12 concern.
14. **PLAN.md commits to an "in-process schema registry stub" in Phase 12.** Chapter 11's "Schema Registries" section discusses external servers only. Implementation has full freedom on the in-process stub's shape; the Session 0003 registry is the seed.
15. **Chapter 11's `EventEnvelope.Payload` is implicitly string** (the code does `JsonNode.Parse(envelope.Payload)`). The implementation's `EventEnvelope.Payload` is typed as `IDomainEvent`. Reconcilable as wire-side vs boundary-side views with the adapter as the bridge.
16. **ADR 0004 (self-contained event store adapters) shipped as commit fb5da10 prior to Session 0003.** Session 0003 embodies the principle for the PostgreSQL adapter; the future SQL Server adapter session will demonstrate the principle's duplication-vs-shared trade-off concretely.
17. **Chapter 8's outbox section should clarify that the outbox is implemented per-adapter** (not as a shared component), demonstrated by the PostgreSQL and SQL Server adapters. The apparent duplication is intentional per ADR 0004.
18. **Chapter 8's `ConcurrencyException` has inconsistent constructors across code samples** (two-argument in the append catch block, one-argument string in the retry-exhaustion path). Implementation ships a single two-argument constructor. The retry-exhaustion path in Chapter 8's `RetryingCommandHandler` needs a different exception type when that work ships in Phase 2; suggested name `RetryBudgetExhaustedException`.
19. **Chapter 8's `ReadStreamAsync` has both 2-arg and 3-arg signatures across different code samples.** Implementation ships the 3-arg form with `fromVersion = 0` default subsuming both cases. Chapter should unify the two signatures or note the 2-arg as a convenience overload.
20. **Chapter 8's "Publication of Events" recommends "outbox + broker".** v1 is "outbox + in-process bus." Same outbox table, different consumer. Chapter should clarify that the outbox supports both configurations.
21. **Chapter 8's Marten section needs revision.** Marten stays as a PostgreSQL-specific alternative (not a shipped peer), and the section needs to acknowledge that "hand-rolled relational" is a category containing both PostgreSQL and SQL Server.
22. **Forward reference (open).** Chapter 8 commits to AES-GCM and `EncryptedString { Ciphertext, KeyId }` for PII fields. Implementation honors when PII-carrying events ship in later phases.
23. **Forward reference (open).** Chapter 8's Process Manager pattern code commits class shapes `EmailUniquenessProcessManager`, `IEmailRegistry.TryClaimAsync` with `(IsFirst, WinnerId)`, and `RevokeEmailRegistration`. Implementation honors when uniqueness work ships in Phase 5.
24. **Forward reference (open).** Chapter 8 commits DynamoDB partition key `AggregateType#AggregateId`, sort key `Version`, concurrency via `ConditionExpression: attribute_not_exists(Version)`. Implementation honors when DynamoDB adapter ships in Phase 11.
25. **SQL Server promotion to first-class peer.** Track A scope expansion. Chapter 8 needs a SQL Server section in "Choosing an Event Store," a SQL Server schema variant in the event store discussion, SQL Server-specific notes in the outbox discussion (filtered indexes vs partial indexes, error 2627 for unique violations), and the optimistic-concurrency unique-constraint error translation. PLAN.md updated to four-peer list (already done in commit fb5da10). Cross-references in later chapters need checking for "three peers" framing.

## Test count

| Checkpoint | Domain.Tests | Infrastructure.Tests | Total |
| --- | --- | --- | --- |
| Session 0002 baseline (start of 0003) | 22 | 10 | 32 |
| After pre-step commit a50931b | 22 | 10 | 32 |
| After registry commit c962cf0 | 22 | 18 | 40 |
| After adapter commit f68eadf | 22 | 30 | 52 |

Session 0003 added 20 tests: 8 `EventTypeRegistryTests`, 7 `PostgresEventStore_AppendAsync_Tests`, 5 `PostgresEventStore_ReadStreamAsync_Tests`.

Infrastructure.Tests warm-cache duration went from 2.0s (Session 0002 baseline) to 3.0s, consistent with the Session 0002 expectation that container startup amortizes across test classes that share `PostgresFixture` via `IClassFixture`. Three new test classes added without re-paying the ~3.5s container cold-start cost per class. If a future session shows the cold-cache number jumping by multiples of 3.5s for no obvious reason, that is the trigger to introduce `[CollectionDefinition]` cross-class fixture sharing.

## Deviations from the session instruction

**`EventStore.Postgres.csproj` ProjectReference to `Domain.Abstractions`.** The session instruction did not anticipate this. Session 0002's `MigrationRunner` and `EventStore.Postgres.Cli` did not need `IDomainEvent`, so the reference had never been added. `EventTypeRegistry`'s `Register<TEvent> where TEvent : IDomainEvent` constraint surfaced it at first build. Single-line csproj addition; landed in the same commit as the registry.

No other deviations. The proposal-confirmation flow caught the registry-vs-envelope precedence and the duplicate-registration exception type ahead of implementation, so the code matched the resolved design without rework.

## Notes for the next sessions

**Session 0004 (OutboxProcessor).** Drains `event_store.outbox` to the in-process bus, with backoff via `next_attempt_at`, error capture in `last_error`, and move-to-quarantine on poison messages. Per the Session 0002 reminder: `outbox_quarantine.attempt_count` is NOT NULL with no default; the quarantine path must read the live outbox row's `attempt_count` and copy it into the quarantine INSERT.

**Later in Phase 2: SQL Server adapter session.** Parallel hand-rolled relational implementation in `src/Infrastructure/EventStore.SqlServer/`. Self-contained per ADR 0004 with its own row construction, INSERT T-SQL, concurrency-violation translation (error 2627 on the equivalent unique constraint), and outbox mechanics. The test suite mirrors Session 0003's structure under `tests/Infrastructure.Tests/SqlServer/`. The PostgresEventStore is the structural template; the SQL Server implementation is not a shared layer.

**EventTypeRegistry composition-root wiring.** The registry is currently constructed in test code only. Composition-root registration arrives with the Application command pipeline in Phase 2; the registry's location moves to `Infrastructure/Versioning` in Phase 12 when the upcaster pipeline arrives. Both moves are forward references; the current location holds in the interim.

**EventStoreRepository metadata population still uses placeholders** (Session 0001 flag 5). Real metadata flows when `ICommandContext` lands in the Phase 2 Application command pipeline. The `PostgresEventStore` round-trip test confirms that whatever metadata the repository hands the adapter survives the JSONB round trip; the test does not validate the *content* of the metadata, only the round-trip.
