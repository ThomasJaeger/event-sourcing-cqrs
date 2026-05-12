# Cross-track flags summary

A consolidated index of every cross-track flag accumulated against the book manuscript through Session 0004. Each flag captures a discrepancy between the manuscript and the working reference implementation, surfaced during the implementation work but not yet addressed on the book side.

This document is the input artifact for Track A reconciliation passes. It does not replace the originating session logs — those carry the full discovery context. This summary normalizes the flags into a single working list so a reconciliation session can scope, prioritize, and check off without reading four session logs end to end.

## How this document is maintained

- New flags discovered during a session are recorded in that session's `docs/sessions/NNNN-*.md` log first.
- After the session ships, the flags are appended here with a fresh ID and a back-pointer to the originating session.
- When Track A reconciles a flag on the manuscript side, the status here updates to `resolved` with the manuscript commit hash (or session log reference) that closed it.
- Flags marked `deferred` are real but waiting on a downstream session (typically because the implementation work the flag references hasn't shipped yet).
- Flags marked `superseded` were valid at the time but have been overtaken by a later decision; the superseding flag carries the current shape.

Status values: `open`, `resolved`, `deferred`, `superseded`.

## Scope of accumulated flags

Sessions covered:

- Session 0001 — Phase 1 in-memory foundation
- Session 0002 — PostgreSQL adapter setup (schema, migration runner, CLI)
- Session 0003 — PostgreSQL `IEventStore` adapter implementation
- Session 0004 — PostgreSQL `OutboxProcessor` and composition root

Sessions 0002 and 0003 generated overlapping flag lists; Session 0003's list is the carry-forward source of truth for flags 1–10 because it adds context the original Session 0002 entries did not have. This document deduplicates against Session 0003's enumeration.

Total flags: 39 across the four sessions (Session 0001: 3 manuscript-actionable; Session 0003: 25, of which 10 carried forward from Session 0002; Session 0004: 8; plus 3 forward-reference holds from Session 0003 carried as `deferred` until the relevant implementation work ships).

## Flags index

### Session 0001 origin — Phase 1 in-memory foundation

| ID | Status | Chapter | Section | Summary |
| --- | --- | --- | --- | --- |
| F-0001-A | open | PLAN.md | Phase 1 schedule | PLAN.md Phase 1 lists `ISnapshotStore` and `IProjectionCheckpoint` ports; neither ships in Phase 1. Update PLAN.md wording to reflect actual delivery (snapshots Phase 12, projection checkpoints Weeks 5-6). |
| F-0001-B | open | Ch 16 | Testing chapter, `OrderTests` example | `OrderPlaced` constructor arity is wrong in Ch 16 (three args) versus Ch 9 (four args including `Money Total`). Ch 9 is authoritative; implementation matches Ch 9. Update Ch 16 example. |
| F-0001-C | open | Ch 16 | `AggregateTest<T>.Then` helper | The printed helper omits `.RespectingRuntimeTypes()`. With FluentAssertions v7 the comparison throws `InvalidOperationException` because both subject and expectation carry the `IDomainEvent` marker as their static element type. Two options: add the call to the printed helper, or rewrite the example as a per-index loop comparison to remove the FluentAssertions-version coupling. |

Note: Session 0001 flags 1 (deferred `ISnapshotStore` and `IProjectionCheckpoint`), 2 (`EventMetadata.ForCommand` factory deferred to Phase 2), 4 (in-memory store is teaching scaffolding, not a fourth peer), and 5 (`EventStoreRepository` metadata placeholders pending the Application command pipeline) were captured in the session log but are not Track A flags. They are forward references to later implementation phases that don't require manuscript changes today. Phase 14 reconciliation may surface manuscript polish, but the current work is implementation-side only.

### Session 0003 origin — figure-discrepancy reconciliation (carry-over from Session 0002)

These ten flags first surfaced in Session 0002's schema work and were re-captured in Session 0003's log with fuller discovery context. The Session 0003 wording is the canonical version.

| ID | Status | Chapter | Section | Summary |
| --- | --- | --- | --- | --- |
| F-0003-01 | open | Ch 8 | Figure 8.2 | `IX_Events_Stream` is declared alongside the `(StreamId, Version)` UNIQUE constraint. The UNIQUE constraint already backs an index; the named index is redundant. Remove from the figure. |
| F-0003-02 | open | Ch 8 | Figure 8.2 | `VARBINARY(MAX)` shown for `Payload` and `Metadata`. The Event Metadata section's prose says JSONB. Update the figure's column type to JSONB to match the prose and the implementation. |
| F-0003-03 | open | Chs 8, 13, 14, 16, Part 4 | `GlobalSequence` vs `GlobalPosition` | Naming inconsistency across multiple chapters, and envelope-vs-metadata placement inconsistency. Implementation commits to `envelope.GlobalPosition`; schema column is `global_position`. Normalize across all five locations. |
| F-0003-04 | open | Ch 8 | "Choosing an Event Store" | Marten is named as a peer alongside PostgreSQL, KurrentDB, DynamoDB. Implementation ships four hand-rolled peers (PostgreSQL, SQL Server, KurrentDB, DynamoDB). Marten reads as a swap-in alternative for the PostgreSQL slot, not a shipped adapter. |
| F-0003-05 | open | Ch 17 | Investigation SQL | Uses `ORDER BY OccurredUtc`. `ORDER BY global_position` is stricter (monotonic, never tied) and matches how the implementation orders the events table. Optional polish. |
| F-0003-06 | open | Ch 8 | Figure 8.4 | `EventType` is missing from the Outbox table despite the OutboxProcessor pseudocode logging `msg.EventType`. Add `event_type` to the figure. |
| F-0003-07 | open | Ch 8 | Figure 8.4 | Timestamp column is named `CreatedUtc`. Chapter 17's outbox-depth metric uses `occurred_at`. Implementation uses `occurred_utc` to match the events table and the surrounding prose. Normalize the figure name. |
| F-0003-08 | open | Ch 8 | Figure 8.4 | No `LastError` or backoff column despite the surrounding prose committing to both. Add `last_error` and `next_attempt_at` to the figure. |
| F-0003-09 | open | Ch 8 | Figure 8.4 | Partial index is `ON Outbox(SentUtc) WHERE SentUtc IS NULL`. The indexed column is always NULL within the filter. Implementation uses `ON event_store.outbox (outbox_id) WHERE sent_utc IS NULL` so the index orders pending rows in FIFO sequence. Correct the figure. |
| F-0003-10 | open | Ch 8 | Near Figure 8.2 | Chapter 8 doesn't mention migration runners or the operational concern of applying schemas to the event store. One paragraph near Figure 8.2 would close the gap and make the schema feel applyable rather than only declarable. The reference implementation uses hand-rolled SQL files plus a small Npgsql-based runner; the paragraph can name the pattern without prescribing a tool. |

### Session 0003 origin — new flags from the IEventStore adapter work

| ID | Status | Chapter | Section | Summary |
| --- | --- | --- | --- | --- |
| F-0003-11 | open | Ch 11 | Event Versioning, `DeserializeCurrent` | The resolver is referenced but never named or defined. Implementation introduces `EventTypeRegistry` to fill the role; currently in `Infrastructure/EventStore.Postgres`, moves to `Infrastructure/Versioning` in Phase 12 alongside the upcaster pipeline. Manuscript should name the role and the interface even if the implementation location is left as a forward reference. |
| F-0003-12 | deferred | Ch 11 | `UpcastingDeserializer` | Takes a single `int currentVersion` in its constructor. This doesn't generalize across multiple event types each at their own current version. Phase 12 concern; revisit when the upcaster pipeline ships. |
| F-0003-13 | deferred | Ch 11 | `IUpcasterFanOut` | The interface is introduced for splits but the unified `Deserialize` signature returning `IEnumerable<IDomainEvent>` for both single-event and fan-out paths is never shown. Phase 12 concern. |
| F-0003-14 | deferred | Ch 11 | "Schema Registries" section | Discusses external servers only. PLAN.md commits to an "in-process schema registry stub" in Phase 12; the Session 0003 `EventTypeRegistry` is the seed. Phase 12 concern. |
| F-0003-15 | open | Ch 11 | `EventEnvelope.Payload` | Implicitly typed as string (the code does `JsonNode.Parse(envelope.Payload)`). The implementation's `EventEnvelope.Payload` is typed as `IDomainEvent`. Reconcilable as wire-side versus boundary-side views with the adapter as the bridge; one paragraph would close the gap. |
| F-0003-16 | open | Ch 8 | Outbox section | ADR 0004 (self-contained event store adapters) shipped as commit fb5da10 prior to Session 0003. Chapter 8's outbox section should clarify that the outbox is implemented per-adapter (not as a shared component), demonstrated by the PostgreSQL and SQL Server adapters. The apparent duplication is intentional. |
| F-0003-17 | open | Ch 8 | Outbox section | Same as F-0003-16 — Chapter 8's outbox section needs to make the per-adapter implementation explicit. Listed separately because the original session log captured this as a distinct discovery in the planning conversation. Reconciliation may merge with F-0003-16 in one edit. |
| F-0003-18 | open | Ch 8 | `ConcurrencyException` and `RetryingCommandHandler` | `ConcurrencyException` has inconsistent constructors across code samples (two-argument in the append catch block, one-argument string in the retry-exhaustion path). Implementation ships a single two-argument constructor. The retry-exhaustion path needs a different exception type when that work ships in Phase 2 (suggested name: `RetryBudgetExhaustedException`). |
| F-0003-19 | open | Ch 8 | `ReadStreamAsync` signatures | Both 2-arg and 3-arg signatures appear across different code samples. Implementation ships the 3-arg form with `fromVersion = 0` default subsuming both cases. Unify the signatures or note the 2-arg as a convenience overload. |
| F-0003-20 | open | Ch 8 | "Publication of Events" | Recommends "outbox + broker". v1 is "outbox + in-process bus." Same outbox table, different consumer. Clarify that the outbox supports both configurations. |
| F-0003-21 | open | Ch 8 | Marten section | Marten stays as a PostgreSQL-specific alternative (not a shipped peer). The section needs to acknowledge that "hand-rolled relational" is a category containing both PostgreSQL and SQL Server. |
| F-0003-22 | deferred | Ch 8 | PII fields | Commits to AES-GCM and `EncryptedString { Ciphertext, KeyId }`. Implementation honors when PII-carrying events ship in later phases. No manuscript change needed today; flagged so the deferral is visible. |
| F-0003-23 | deferred | Ch 8 | Process Manager pattern | Commits class shapes `EmailUniquenessProcessManager`, `IEmailRegistry.TryClaimAsync` with `(IsFirst, WinnerId)`, and `RevokeEmailRegistration`. Implementation honors when uniqueness work ships in Phase 5. No manuscript change today. |
| F-0003-24 | deferred | Ch 8 | DynamoDB partition keys | Commits partition key `AggregateType#AggregateId`, sort key `Version`, concurrency via `ConditionExpression: attribute_not_exists(Version)`. Implementation honors when DynamoDB adapter ships in Phase 11. No manuscript change today. |
| F-0003-25 | open | Ch 8 | Multiple sections | SQL Server promotion to first-class peer. Needs a SQL Server section in "Choosing an Event Store," a SQL Server schema variant in the event store discussion, SQL Server-specific notes in the outbox discussion (filtered indexes versus partial indexes, error 2627 for unique violations), and the optimistic-concurrency unique-constraint error translation. PLAN.md was updated to four-peer list in commit fb5da10; the manuscript catches up here. Cross-references in later chapters need checking for "three peers" framing. |

### Session 0004 origin — `OutboxProcessor` design and implementation

| ID | Status | Chapter | Section | Summary |
| --- | --- | --- | --- | --- |
| F-0004-01 | open | Ch 8 | Publication of Events, processor code | `OutboxProcessor` shown is polling-only with no `LISTEN/NOTIFY` and no backoff column. Implementation is polling-only too, but with `next_attempt_at`-driven backoff in the WHERE clause and `last_error` capture in the failure path. One-sentence acknowledgment near the processor code closes the gap. `LISTEN/NOTIFY` properly lands in the Phase 6 projection-trigger session. |
| F-0004-02 | open | Ch 8 | Publication of Events, processor code | Reads pending rows without a lock hint. Implementation adds `FOR UPDATE SKIP LOCKED`. Acknowledge as the standard relational pattern. Same flag applies to SQL Server (`READPAST` + `UPDLOCK` equivalent) for the SQL Server section the manuscript will gain via F-0003-25. |
| F-0004-03 | open | Ch 8 | Publication of Events, processor code | Increments `AttemptCount` but never computes or persists a next-attempt time, so the "exponential backoff" the prose commits to isn't realized in the code shown. Short paragraph: production processors compute a scheduled next-attempt time; exponential with jitter is the standard formula; the reference implementation uses base 1s, cap 5min, full jitter. |
| F-0004-04 | open | Ch 8 | Publication of Events, processor code | `QuarantineAsync` is shown as an opaque call. Implementation realizes it as a `DELETE ... RETURNING ... INSERT` CTE that structurally carries `attempt_count` and `last_error` rather than reading-then-rebinding them in C#. One-line note near the code. SQL Server uses `OUTPUT INTO` rather than CTE-with-RETURNING but achieves the same shape. |
| F-0004-05 | open | Ch 8 | Publication of Events, failure-modes prose | Describes startup recovery as "the processor sees in-flight rows older than a timeout threshold and re-dispatches them," which presumes an in-flight column. The reference implementation eliminates the in-flight column and the threshold by holding "in-flight" as a row lock inside an open transaction. Acknowledge: "implementations that scope dispatch inside a `SELECT ... FOR UPDATE SKIP LOCKED` transaction get this recovery for free." Both shapes remain valid; the manuscript can note the trade-off. |
| F-0004-06 | open | Ch 8 | Publication of Events, processor code | `IMessageDispatcher` is referenced in the processor code but never defined. Implementation defines it in `Domain.Abstractions` with a typed-envelope payload (`OutboxMessage` carrying `IDomainEvent`, `EventMetadata`, `OutboxId`, `AttemptCount`). Short paragraph naming the interface, its method shape, and the envelope type. Same flag covers `IEventHandler<TEvent>`, also referenced but never declared. |
| F-0004-07 | open | Ch 8 | Publication of Events, processor code | Uses `await Task.Delay(...)` inline with `DateTime`-implicit time. The reference implementation injects `TimeProvider` and a jitter source via an `OutboxProcessorOptions` class. One sentence: "production processors take a `TimeProvider` and a jitter source so the backoff schedule is unit-testable without wall-clock dependence." |
| F-0004-08 | open | Ch 8 | Publication of Events, near the end of the section | Processor takes `IDbConnectionFactory`, `IMessageDispatcher`, and `ILogger` via constructor injection but never shows composition-root wiring or names the DI extension method. Half-page "Wiring it up" section showing host-side registration (e.g., `AddPostgresEventStore`) without prescribing a DI framework. Same flag applies to the SQL Server adapter (`AddSqlServerEventStore`, `ISqlConnectionFactory`). |

## Concentration

Of the 39 flags, the manuscript-actionable subset (`open` status) lands almost entirely on Chapter 8. Specifically:

- Chapter 8: 31 flags (10 figure-discrepancy carry-overs, 13 from Session 0003 new, 8 from Session 0004)
- Chapter 11: 2 flags currently open (F-0003-11 and F-0003-15), 3 deferred to Phase 12
- Chapter 16: 2 flags (F-0001-B and F-0001-C)
- Chapter 17: 1 flag (F-0003-05, optional polish)
- PLAN.md: 1 flag (F-0001-A)
- Cross-chapter (multiple): 2 flags (F-0003-03 spans Chs 8/13/14/16/Part 4; F-0003-25 spans Ch 8 plus cross-references)

This concentration shapes the reconciliation pass. Chapter 8 carries the bulk of the work and is the natural batch boundary. Chapter 11 has two open flags but most of its action sits in deferred territory until Phase 12 ships. Chapters 16, 17, and PLAN.md are small, isolated edits.

## Recommended Track A pass sequencing

A single reconciliation pass against all 31 Chapter 8 flags is the right scope. The flags fall into natural clusters that can be addressed in one editing arc:

1. **Figure 8.2 and Figure 8.4 corrections** — F-0003-01, F-0003-02, F-0003-06, F-0003-07, F-0003-08, F-0003-09. Six figure edits. Mechanical once the figure source files are open.
2. **Schema and migration paragraph** — F-0003-10. One new paragraph near Figure 8.2 introducing the migration-runner pattern.
3. **`OutboxProcessor` code listing and surrounding prose** — F-0004-01 through F-0004-08. Eight related acknowledgments that update the same code listing and its accompanying prose to match the reference implementation. Best done as a single revision of the Publication of Events section rather than eight separate edits.
4. **Per-adapter outbox framing** — F-0003-16, F-0003-17, F-0003-20, F-0003-21. Four flags clarifying that the outbox is per-adapter (not shared), supports broker and in-process consumers, and that Marten reads as alternative not peer.
5. **SQL Server promotion** — F-0003-25. The largest single edit. Adds a SQL Server section to "Choosing an Event Store," plus T-SQL-specific notes throughout the outbox discussion. Pairs naturally with F-0004-02 and F-0004-04 (both flag SQL Server equivalents already).
6. **`ConcurrencyException` and `ReadStreamAsync` unifications** — F-0003-18, F-0003-19. Code-listing edits in the IEventStore section.
7. **Cross-chapter `GlobalPosition` normalization** — F-0003-03. Spans five locations across Chs 8, 13, 14, 16, Part 4. Best handled as a find-and-update sweep rather than five separate edits.

The Chs 16, 17, and Ch 11 flags can ride alongside the main Chapter 8 pass, or be addressed in a small follow-up pass after Chapter 8 is reconciled. The flag count is small enough that batching with Chapter 8 is reasonable.

## Notes for the Track A planning session

When this index is the input to a planning session in Claude.ai:

- Treat the flags table as the working list. Update status inline as flags are addressed.
- Some flags interact. F-0003-04 (Marten as peer) and F-0003-21 (Marten section needs revision) reference the same Marten material; they may resolve in a single edit. Similarly, F-0003-16 and F-0003-17 are likely one edit.
- Forward-reference flags (F-0003-22, F-0003-23, F-0003-24, F-0003-12, F-0003-13, F-0003-14) stay deferred; the planning session can confirm the deferrals are still correct given the current PLAN.md state but should not attempt to act on them prematurely.
- Voice rules from `HANDOFF.md` apply throughout. Em-dashes are forbidden in prose. Filler intensifiers are forbidden. Bold lead-ins are encouraged for parallel subsections. First-person opinions are encouraged. The reconciliation prose must match the established voice of the chapter being edited.
- The Track A planning session's job is to draft the reconciled prose with voice rules enforced inline. The Track C execution session in the book repo inserts the drafted prose into the right `content/content_*.js` location via the existing build helpers (`h1`, `h2`, `h3`, `p`, `prose`, `code`, `figure`) and runs the two-pass build to verify.
