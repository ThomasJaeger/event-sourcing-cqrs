# Session 0004: Weeks 3-4 PostgreSQL OutboxProcessor

Date: 2026-05-12
Phase: 2 (Weeks 3-4) per PLAN.md; Session 3 of 3 in the reconciled six-week plan's PostgreSQL slice.

## Status

Shipped 2026-05-12. Track C executed against the design record in four commits.

1. `be8ef46` — IMessageDispatcher, OutboxMessage, IEventHandler contracts in Domain.Abstractions
2. `86ae4c4` — OutboxRetryPolicy with 16 xUnit cases across 2 test methods
3. `e5b361d` — OutboxProcessor with backoff, quarantine, and shared dispatcher (8 integration tests)
4. `18b4df7` — AddPostgresEventStore composition-root extension (1 DI-resolution test)

Sections below describe both the design as agreed in Track B and the implementation as shipped. Where execution refined a design decision, the section text reflects what shipped; the "Deviations from the design record" section catalogues each refinement with its reason.

## Scope

The `OutboxProcessor` that drains `event_store.outbox` to the in-process event bus, with `next_attempt_at`-driven exponential backoff, `last_error` capture, and atomic move-to-quarantine for messages exceeding `MaxAttempts`. The `IMessageDispatcher` abstraction and the `InProcessMessageDispatcher` implementation that resolves zero-to-many `IEventHandler<TEvent>` from DI. The `OutboxRetryPolicy` that owns the backoff formula. The `AddPostgresEventStore` composition-root extension that wires all of the above plus the `PostgresEventStore` from Session 0003. Integration tests against `PostgresFixture`, unit tests for the policy, one DI-resolution test.

Out of scope for this session: the Workers host that consumes the registration extension as a `BackgroundService`; the actual handler population on `EventTypeRegistry` (the registry exists, the registration calls land alongside the first host or the first projection); `LISTEN/NOTIFY` wake-up signaling on the outbox; broker-side dispatch.

## Design decisions

### Q1: Dispatch trigger — polling-only

Polling matches Chapter 8's reference `OutboxProcessor`, which loops, sleeps 500ms on an empty batch, and 5s on exception. No `LISTEN/NOTIFY`, no broker hook.

The current build state argues the same way. v1's `IMessageDispatcher` dispatches in-process. The downstream the outbox is feeding — projections — doesn't land until Phase 6. Until then the outbox drains into test doubles and into an empty handler set. The 500ms idle wakeup is invisible to a user who doesn't exist.

The "both" option (`LISTEN/NOTIFY` doorbell over a polling backstop) is a real design and a near-certain Phase 6 win. It belongs in the Phase 6 projection-trigger session, where the same mechanism benefits both the outbox processor and the projection trigger and where the latency starts mattering for real UX. PLAN.md already flags `LISTEN/NOTIFY` as a stretch goal there. Doing it twice — Session 0004 for outbox, Phase 6 for projections — is two near-identical implementations and a small abstraction debate about whether they should share code. Per ADR 0004, they shouldn't. Per the duplication smell, maybe they should. Defer the question to the session that forces it.

The pending-row query honors the Session 0002 schema additions (`next_attempt_at`, `last_error`) that don't appear in Figure 8.4:

```sql
WHERE sent_utc IS NULL
  AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
ORDER BY outbox_id
LIMIT @batch_size
```

The existing `ix_outbox_pending` partial index covers the predicate and the FIFO ordering; the `next_attempt_at` filter is evaluated per candidate row, cheap at expected backlog sizes.

### Q2: Read query, lock mode, ordering

`FOR UPDATE SKIP LOCKED`, single transaction per batch, FIFO by `outbox_id`.

```sql
SELECT outbox_id, event_id, event_type, payload, metadata, attempt_count
FROM event_store.outbox
WHERE sent_utc IS NULL
  AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
ORDER BY outbox_id
LIMIT @batch_size
FOR UPDATE SKIP LOCKED;
```

`SKIP LOCKED` is defense-in-depth even though v1 ships a single `OutboxProcessor` in one Workers host. The cost is zero in the happy path; the protection covers accidental second instances during rolling deploys, tests spinning up parallel processors against the same fixture, and operators running the processor manually while the host is still up. Chapter 8's code doesn't show this hint but doesn't forbid it; the at-least-once contract the chapter commits to is preserved.

Transaction boundary: BEGIN, `SELECT FOR UPDATE SKIP LOCKED`, per-message dispatch and UPDATE (SENT or attempt+last_error+next_attempt_at) inside the same transaction, COMMIT. Locks held during dispatch. For v1's in-process dispatcher, total dispatch time for a batch of 100 is sub-millisecond, so the lock-during-dispatch concern doesn't bite. When the broker arrives post-v1, the pattern needs a `dispatching_until` lease column so locks don't span network calls. That's the broker session's problem, not this one.

FIFO is preserved within a single processor. Across processors with `SKIP LOCKED` it isn't, but v1 runs one. Retries within a batch can deliver out-of-order on the wire if message N fails and N+1 succeeds — the book's posture is "subscribers must be idempotent" and projections in Phase 6 will maintain checkpoints with version checks that handle this case.

### Q3: Backoff schedule — exponential with full jitter

Base 1s, cap 5min (300s), full jitter window `[0, delay]`.

```
delay_seconds = min(2 ^ (attempt_count - 1), 300)
next_attempt_at = utcNow + random(0, delay_seconds)
```

The cap activates at attempt 10 (2^9 = 512 > 300) and is forward-defensive against a future `MaxAttempts` increase. Under v1's `MaxAttempts = 10` the processor quarantines before the policy is invoked at the cap boundary, so the curve the policy actually exercises in production runs attempts 1..9 with raw delays of 1, 2, 4, 8, 16, 32, 64, 128, 256 seconds.

Worst-case time from first failure to quarantine is ~8.5 minutes, average ~4. Transient blips clear in the early attempts; a broker-down outage gets a handful of patient retries before an operator-visible quarantine.

Full jitter (not equal jitter, not no jitter) handles the thundering-herd case where a subscriber recovers and the entire in-flight batch falls on the same exponential ladder. AWS Architecture Blog's "Exponential Backoff and Jitter" is the standard reference.

`next_attempt_at` is computed in C# against an injected `TimeProvider`, not in SQL via `now() + interval`. Deterministic unit tests need a fake clock and an injected jitter source (a `Func<double>` returning `[0, 1)`). The policy lives in one place — `OutboxRetryPolicy` — rather than split across C# constants and SQL expressions.

`OutboxRetryPolicy` lives in `Infrastructure.EventStore.Postgres`, not `Domain.Abstractions`. The outbox is per-adapter per ADR 0004; the SQL Server adapter session will write its own policy or share this one explicitly. Keeping the policy adapter-local matches the per-adapter principle.

### Q4: Quarantine — atomic CTE move, book ordering preserved

```sql
WITH moved AS (
  DELETE FROM event_store.outbox
  WHERE outbox_id = @outbox_id
  RETURNING outbox_id, event_id, event_type, payload, metadata,
            occurred_utc, attempt_count, last_error
)
INSERT INTO event_store.outbox_quarantine
  (outbox_id, event_id, event_type, payload, metadata,
   occurred_utc, attempt_count, final_error, quarantined_at)
SELECT outbox_id, event_id, event_type, payload, metadata,
       occurred_utc, attempt_count, last_error, @now
FROM moved;
```

The live `outbox.last_error` column is renamed to `final_error` on the way into `outbox_quarantine` because the row is no longer in retry. `@now` is bound from the injected `TimeProvider` so FakeTimeProvider drives quarantine timestamps deterministically; the schema's `quarantined_at DEFAULT now()` is the fallback when no parameter is supplied.

One round trip, atomic. The Session 0002 reminder — `outbox_quarantine.attempt_count` is NOT NULL with no default, easy to forget to carry — is structurally answered here. `attempt_count` is never constructed by C# code; it's carried by SQL out of `DELETE ... RETURNING`. The footgun is closed at the schema-plus-query level rather than at the "remember to" level.

The CTE runs inside the batch transaction from Q2. The increment-then-check-then-quarantine ordering matches Chapter 8: `IncrementAttemptAsync`, check `msg.AttemptCount + 1 >= MaxAttempts`, quarantine. The UPDATE on a row about to die is a microscopic waste; splitting the failure-path logic in two to avoid it isn't worth it. Bonus: the post-increment `attempt_count` reads as `MaxAttempts` exactly in the quarantine row ("10 of 10 attempts exhausted").

`event_id UNIQUE` on `outbox_quarantine` — already present from Session 0002 — handles the "operator re-queued and it failed again" case correctly: the second move surfaces as a unique violation, which is the right signal.

Logging is `LogCritical` with structured properties covering `outbox_id`, `event_type`, `event_id`, `correlation_id` (read from the row's STORED generated column), `max_attempts`, and `last_error`. No metric counter, no emitted domain event. The AdminConsole projection-status dashboard reads `outbox_quarantine` directly for the operator surface in Phase 7.

Operator re-queue is out of scope for Session 0004 mechanically — the schema supports it, the AdminConsole Replay Tool exposes it in Phase 7.

### Q5: Crash recovery — none needed, structurally

Chapter 8 frames crash recovery as "the processor sees in-flight rows older than a timeout threshold and re-dispatches them," which presumes an in-flight column. This design doesn't have one. There are exactly two terminal states on an outbox row: pending and sent. The "in-flight" condition lives entirely inside a `FOR UPDATE` row lock held by an open transaction. When the backend dies, Postgres releases the lock and the row reverts to pending without any cleanup code on our side.

The three Chapter 8 crash points all reduce to the same handling. Crash mid-dispatch: transaction rolls back, row stays pending, restart re-dispatches. Crash between dispatch return and SENT mark commit: same. Crash mid-COMMIT: Postgres makes COMMIT atomic, so either the batch's SENT marks land in WAL or they don't, with no torn-batch state. In every case: at-least-once exactly as Chapter 8 commits, duplicates absorbed by idempotent subscribers.

No reaper, no staleness check, no threshold, no cleanup migration. The architecture earns its keep here.

This breaks the moment dispatch acquires a long-held resource (broker session, remote transaction, HTTP call with a 30s timeout) while the row lock is held. Q2's "locks held during dispatch" note already flagged this for the post-v1 broker session — a `dispatching_until` lease column and lock-release-before-network is the standard refactor when it lands.

Shutdown: the `BackgroundService` stopping token flows through every async call. Cancellation mid-batch throws `OperationCanceledException`, the transaction aborts cleanly, the service exits. Same recovery path as crash. The `ExecuteAsync` filter on `!ct.IsCancellationRequested` swallows cancellation as expected-shutdown rather than logging it as an error.

### Q6: `IMessageDispatcher` and `IEventHandler<TEvent>`

`IMessageDispatcher` lives in `Domain.Abstractions`. Payload is a typed envelope. Returns `Task`, throws on failure. One implementation in this session, in a new `src/Infrastructure/Outbox/` project. The design record initially left the implementation's project location implicit; the resolved placement keeps the engine-agnostic dispatcher in a project both the PostgreSQL and the future SQL Server adapter can share without violating ADR 0004 (the ADR governs engine-specific outbox mechanics — SQL, connection acquisition, retry policy, the processor itself — not consumer-side handler resolution).

```csharp
namespace EventSourcingCqrs.Domain.Abstractions;

public interface IMessageDispatcher
{
    Task DispatchAsync(OutboxMessage message, CancellationToken ct);
}

public sealed record OutboxMessage(
    long OutboxId,
    Guid EventId,
    string EventType,
    IDomainEvent Event,
    EventMetadata Metadata,
    int AttemptCount);

public interface IEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct);
}
```

`Domain.Abstractions` because the contract is cross-cutting. The PostgreSQL `OutboxProcessor` and the eventual SQL Server `OutboxProcessor` both call into `IMessageDispatcher`. Per ADR 0004 the processors are separate; the dispatcher contract they share is what unifies them at the consumer level. Projections in Phase 6 implement `IEventHandler<TEvent>` and resolve through the same dispatcher.

`OutboxMessage` is a typed envelope, not raw bytes, because deserialization is the adapter's job — it owns the wire format. The dispatcher receives an `IDomainEvent` object plus metadata. The `OutboxId` and `AttemptCount` ride along for diagnostic logging in handlers that want them.

`Task`-returning, throws on failure, symmetric with `IEventStore.AppendAsync`. The processor's try/catch from Q3/Q4 pivots on exceptions. A `DispatchResult` enum with a "permanent failure, quarantine immediately" variant is a legitimate future feature, but v1 has no use case for it. Defer.

`InProcessMessageDispatcher` resolves zero-to-many `IEventHandler<TEvent>` from DI by reflection, invokes them in registration order, swallows nothing. Reflection cost is small; the alternative (source-generated dispatch tables) optimizes a path that isn't hot. The shipped implementation caches the resolved `MethodInfo` per event type in a `ConcurrentDictionary` so the per-dispatch reflection cost is one cache lookup plus the closed-generic interface call, not a fresh `GetMethod` traversal per message. If profiling later proves it matters, the contract doesn't change.

```csharp
public sealed class InProcessMessageDispatcher : IMessageDispatcher
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> HandleMethodCache = new();
    private readonly IServiceProvider _services;

    public InProcessMessageDispatcher(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public async Task DispatchAsync(OutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var eventType = message.Event.GetType();
        var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
        var method = HandleMethodCache.GetOrAdd(
            eventType,
            _ => handlerType.GetMethod(nameof(IEventHandler<IDomainEvent>.HandleAsync))!);

        foreach (var handler in _services.GetServices(handlerType))
        {
            await (Task)method.Invoke(handler, new object[] { message.Event, ct })!;
        }
    }
}
```

In Session 0004 the resolved handler list is always empty — no projections exist yet. Every dispatch trivially succeeds. That's the right shape for "the outbox drains into a void until Phase 6 arrives." Projections then register handlers, and dispatch flows without contract changes.

### Q7: Test plan

Eight integration tests against `PostgresFixture` in `Postgres/OutboxProcessorTests.cs`, two unit tests in `Postgres/OutboxRetryPolicyTests.cs` (adapter-local per Q3), one DI-resolution test in `Postgres/ServiceCollectionExtensionsTests.cs`. Eleven methods total. No `Thread.Sleep` anywhere.

Integration tests (call `ProcessBatchAsync` directly; do not exercise the `BackgroundService` lifecycle). Test method names use snake-case-sentence style per the repo convention (`Cancelling_a_shipped_order_throws`), not the PascalCase the design record initially used:

1. `Drains_pending_row_marks_sent` — happy path; row becomes sent, dispatcher called once with deserialized event.
2. `Empty_outbox_returns_zero_and_does_not_call_dispatcher` — empty short-circuit.
3. `Failed_dispatch_increments_attempt_count_schedules_next_attempt_persists_last_error` — failure UPDATE.
4. `Row_with_future_next_attempt_at_is_skipped` — `next_attempt_at` filter against `FakeTimeProvider`.
5. `Exceeds_max_attempts_quarantines_row_preserves_attempt_count` — atomic CTE move; `attempt_count = 3` carried in quarantine row under the test's `MaxAttempts = 3`.
6. `Success_after_prior_failure_persists_sent_despite_attempt_count` — success doesn't reset history.
7. `Dispatches_in_fifo_order_within_batch` — `ORDER BY outbox_id`.
8. `Concurrent_processors_skip_locked_no_double_dispatch` — `FOR UPDATE SKIP LOCKED` defense-in-depth, deterministically interleaved via `TaskCompletionSource` gates rather than wall-clock sleeps.

Unit tests for `OutboxRetryPolicy`:

9. `ComputeNextAttempt_follows_exponential_backoff_with_cap` — `[Theory]` with 15 `[InlineData]` rows covering attempts 1..15; jitter pinned to 1.0 so the asserted delays equal the raw curve.
10. `ComputeNextAttempt_zero_jitter_returns_now` — pins the lower bound of the full-jitter window where Test 9 pins the upper bound. (The original `ComputeNextAttempt_AtMaxAttempts_ReturnsQuarantineSentinel` shape was based on a misreading of the policy's responsibility: the processor branches on the `attempt_count + 1 >= MaxAttempts` check before invoking the policy, so the policy never sees max-attempts as a special input and has no quarantine sentinel to return.)

Composition root test:

11. `AddPostgresEventStore_resolves_registered_services` — extension resolves `IEventStore`, `IMessageDispatcher`, `OutboxProcessor` (as `IHostedService`), `INpgsqlConnectionFactory`, `EventTypeRegistry`, `JsonSerializerOptions`, `OutboxRetryPolicy`. No Postgres dependency; uses `ServiceProvider` only.

Deterministic time and jitter wiring via `OutboxProcessorOptions`:

```csharp
public sealed class OutboxProcessorOptions
{
    public int BatchSize { get; init; } = 100;
    public int MaxAttempts { get; init; } = 10;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;
}
```

`TimeProvider` is .NET stdlib; `Microsoft.Extensions.TimeProvider.Testing` ships `FakeTimeProvider` with `Advance(TimeSpan)`. Jitter is a `Func<double>` returning `[0, 1)`. Tests construct everything by hand, no `IServiceProvider` in the integration test path. `RecordingDispatcher` is a hand-rolled test double in the test project — no `NSubstitute` or `Moq` for this — that records each `OutboxMessage` and supports throw-on-event-type for failure-path tests.

What's not tested here: the `ExecuteAsync` outer loop's `Task.Delay` sleeps (four lines wrapping `ProcessBatchAsync`, covered by inspection per the Session 0003 precedent), `InProcessMessageDispatcher`'s reflection-based resolution (unit-tested separately in Phase 6 when handlers exist).

### Q8: Composition root

One `AddPostgresEventStore` extension in `EventStore.Postgres`. Tests construct directly. The Workers host wires it when it arrives.

```csharp
namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresEventStore(
        this IServiceCollection services,
        Action<PostgresEventStoreOptions> configure)
    {
        services.Configure(configure);
        services.AddOptions<OutboxProcessorOptions>();

        services.TryAddSingleton<NpgsqlDataSource>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PostgresEventStoreOptions>>().Value;
            return NpgsqlDataSource.Create(opts.ConnectionString);
        });
        services.TryAddSingleton(_ => new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        });

        services.AddSingleton<INpgsqlConnectionFactory, NpgsqlConnectionFactory>();
        services.AddSingleton<EventTypeRegistry>();
        services.AddSingleton<IEventStore, PostgresEventStore>();

        services.AddSingleton<OutboxRetryPolicy>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OutboxProcessorOptions>>().Value;
            return new OutboxRetryPolicy(opts.BaseSeconds, opts.CapSeconds);
        });
        services.AddSingleton<IMessageDispatcher, InProcessMessageDispatcher>();
        services.AddHostedService<OutboxProcessor>();

        return services;
    }
}

public sealed class PostgresEventStoreOptions
{
    public string ConnectionString { get; set; } = string.Empty;
}
```

Three refinements over the design record's original sketch.

First, `NpgsqlDataSource` and `JsonSerializerOptions` register via `TryAddSingleton` so a host that pre-registers either one wins. The default `NpgsqlDataSource` is built from `PostgresEventStoreOptions.ConnectionString` via `NpgsqlDataSource.Create` (the modern Npgsql connection-pooling primitive); the sketch's older `new NpgsqlConnection(connStr)` path is superseded. A host that needs custom data-source wiring (logging integration, custom type mappings, multiplexing) registers its own `NpgsqlDataSource` before calling the extension.

Second, `OutboxRetryPolicy` registers via a factory delegate that reads `OutboxProcessorOptions.BaseSeconds`/`CapSeconds` at first resolution. The options class's curve properties are therefore live, not decorative; the host can configure them before calling the extension. The singleton is constructed once, so a `services.Configure<OutboxProcessorOptions>` call after `AddPostgresEventStore` has no effect on the already-built policy.

Third, `AddOptions<OutboxProcessorOptions>()` replaces the design record's `Configure<>(_ => { })` placeholder. Same effect, no no-op lambda.

The extension lives in `EventStore.Postgres`, not a sibling DI-only project. ADR 0004 (self-contained adapters) governs. The SQL Server adapter session will need a parallel `AddSqlServerEventStore` in `EventStore.SqlServer`. The host calls one or the other; switching backends is a one-line change.

Lifetimes are all singleton. `INpgsqlConnectionFactory` wraps an `NpgsqlDataSource`; `IEventStore` opens connections per call through the factory; `IMessageDispatcher` resolves handlers from `IServiceProvider` per dispatch and caches the resolved `MethodInfo` per event type in a static `ConcurrentDictionary`; `OutboxProcessor` is `IHostedService`, singleton by registration semantics. The per-batch scope lives inside a method, not in DI. Scoped lifetimes arrive with the Application command pipeline in Phase 2; the write-side event store doesn't need them.

`INpgsqlConnectionFactory` is PostgreSQL-specific and lives in `Infrastructure.EventStore.Postgres`, not `Domain.Abstractions`. The Npgsql-typed `NpgsqlConnection` return is the reason: lifting the interface to Domain would force a return type of `DbConnection` and lose the typed Npgsql API. Per ADR 0004 the SQL Server adapter declares its own `ISqlConnectionFactory` separately. Adapter-specific factories on the adapter-specific side; tests substitute against the adapter-specific interface.

`EventTypeRegistry` is registered as a singleton. Population happens at host startup (the Workers host's `Program.cs` calls `registry.Register<OrderPlaced>()` etc. after `AddPostgresEventStore`). The two-phase pattern is awkward at v1; the cleaner shape is per-aggregate-module `IEventTypeProvider` contributions composed by a single registry factory. That's a Phase 2 cleanup when the first host arrives, not a Session 0004 decision.

## Cross-track flags (Track A)

1. **Chapter 8's `OutboxProcessor` code is polling-only with no `LISTEN/NOTIFY` and no backoff column.** The implementation is polling-only too, but with `next_attempt_at`-driven backoff in the WHERE clause and `last_error` capture in the failure path. Both are book-extensions the chapter could acknowledge in one sentence near the processor code.

2. **Chapter 8's processor code reads pending rows without a lock hint.** Implementation adds `FOR UPDATE SKIP LOCKED`. The book could acknowledge this as the standard relational pattern: "production implementations use `SELECT ... FOR UPDATE SKIP LOCKED` so multiple processor instances can drain the outbox in parallel without double-dispatch." Same flag applies to the SQL Server session in Phase 2 (`READPAST` + `UPDLOCK` are the T-SQL equivalent).

3. **Chapter 8's `OutboxProcessor` code increments `AttemptCount` but never computes or persists a next-attempt time**, so the "exponential backoff" the prose commits to isn't realized in the code shown. Implementation realizes it via the `next_attempt_at` column added in Session 0002 and a separate `OutboxRetryPolicy` class. The book could close the gap with a short paragraph: production processors compute a scheduled next-attempt time, exponential with jitter is the standard formula, the reference implementation uses base 1s, cap 5min, full jitter.

4. **Chapter 8's `QuarantineAsync` is shown as an opaque call.** Implementation realizes it as a `DELETE ... RETURNING ... INSERT` CTE that structurally carries `attempt_count` and `last_error` rather than reading-then-rebinding them in C#. The book could note this pattern in one line near the processor code. SQL Server uses `OUTPUT INTO` rather than CTE-with-RETURNING but achieves the same shape.

5. **Chapter 8 describes startup recovery as "the processor sees in-flight rows older than a timeout threshold and re-dispatches them,"** which presumes an in-flight column. The reference implementation eliminates the in-flight column and the threshold by holding "in-flight" as a row lock inside an open transaction. The book could acknowledge this as the cleaner relational pattern: "implementations that scope dispatch inside a `SELECT ... FOR UPDATE SKIP LOCKED` transaction get this recovery for free." The "in-flight column plus timeout" alternative is still valid for designs that hold dispatch outside the transaction.

6. **Chapter 8's `IMessageDispatcher` is referenced in the processor code but never defined.** Implementation defines it in `Domain.Abstractions` with a typed-envelope payload (`OutboxMessage` carrying `IDomainEvent`, `EventMetadata`, `OutboxId`, `AttemptCount`). The book could close the gap with a short paragraph naming the interface, its method shape, and the envelope type. Same flag covers `IEventHandler<TEvent>` — referenced in prose, never declared.

7. **Chapter 8's processor code uses `await Task.Delay(...)` inline with `DateTime`-implicit time.** The reference implementation injects `TimeProvider` and a jitter source via an `OutboxProcessorOptions` class. The book could acknowledge this in a sentence: "production processors take a `TimeProvider` and a jitter source so the backoff schedule is unit-testable without wall-clock dependence."

8. **Chapter 8's processor takes `IDbConnectionFactory`, `IMessageDispatcher`, and `ILogger` via constructor injection but never shows composition-root wiring or names the DI extension method.** The reference implementation ships `AddPostgresEventStore`, with `INpgsqlConnectionFactory` (PostgreSQL-specific, not in Domain.Abstractions) and options-pattern configuration. The book could acknowledge this in a "Wiring it up" half-page near the end of the Publication of Events section, showing host-side registration without prescribing a DI framework. Same flag applies to the SQL Server adapter (`AddSqlServerEventStore`, `ISqlConnectionFactory`).

## Test count

The design record projected 11 new tests counting methods. Three numbers are useful when reconciling that projection against test-runner output: the design's projection, the count of test methods actually written, and the xUnit-reported case count (which expands `[Theory]` rows). Legend: **planned methods** = Track B design projection; **methods** = test methods Track C wrote; **xUnit cases** = test-runner total including `[Theory]` row expansion.

| Checkpoint | Domain.Tests | Infrastructure.Tests | Total |
| --- | --- | --- | --- |
| Session 0003 baseline (end of 0003) | 22 | 30 | 52 |
| Session 0004 planned methods | 22 | 41 | 63 |
| Session 0004 actual methods | 22 | 41 | 63 |
| Session 0004 actual xUnit cases | 22 | 55 | 77 |

Method-level breakdown for Session 0004 (11 total, all Infrastructure-side per the per-adapter principle):

- `OutboxRetryPolicyTests`: 2 methods (1 `[Theory]` with 15 `[InlineData]` rows = 15 cases, 1 `[Fact]` = 1 case → 16 cases)
- `OutboxProcessorTests`: 8 methods, 8 cases
- `ServiceCollectionExtensionsTests`: 1 method, 1 case

`Infrastructure.Tests` warm-cache wall time stayed at ~3s against the Session 0003 baseline. The Session 0003 trigger condition for introducing `[CollectionDefinition]` cross-class fixture sharing (cold-cache jumps of 3.5s "for no obvious reason") was not exercised; container reuse via `IClassFixture<PostgresFixture>` continues to absorb the new test class's overhead.

## Deviations from the design record

None of these changed a design decision. Each is a refinement caught during execution that the design record was silent on or mildly wrong about.

**`InProcessMessageDispatcher` project placement.** Q6 originally left the implementation's project implicit. The shipped class lives in a new `src/Infrastructure/Outbox/` project so the future SQL Server adapter session shares the engine-agnostic dispatcher without project-referencing `EventStore.Postgres` or duplicating the class. ADR 0004 governs engine-specific outbox mechanics, not consumer-side handler resolution.

**Q4 CTE column names.** The design record's CTE example used `created_utc`, `last_error` in the INSERT column list, and `quarantined_utc`. The migration-0001 schema uses `occurred_utc`, `final_error`, and `quarantined_at`. The implementation honors the schema; this section's CTE has been updated in place.

**Q3 cap-vs-MaxAttempts clarification.** The cap activates at attempt 10 (2^9 = 512 > 300) and is forward-defensive against a future `MaxAttempts` increase. Under v1's `MaxAttempts = 10` the policy never sees the cap branch in normal operation because the processor quarantines first. Q3 now states this explicitly.

**Q7 test name swap.** `ComputeNextAttempt_AtMaxAttempts_ReturnsQuarantineSentinel` was based on a misreading of the policy's responsibility — the processor checks the max-attempts boundary before invoking the policy, so the policy has no quarantine branch to test. Replaced with `ComputeNextAttempt_zero_jitter_returns_now`, which pins the lower bound of the full-jitter window where the curve test pins the upper. All Q7 test names also moved from PascalCase to the repo's snake-case-sentence convention.

**Q8 refinements.** Three. The composition-root sketch's `new NpgsqlConnection(connStr)` path is superseded by `NpgsqlDataSource.Create(opts.ConnectionString)` inside a `TryAddSingleton`, so a host that pre-registers its own data source wins. `JsonSerializerOptions` also registers via `TryAddSingleton` for the same reason. `OutboxRetryPolicy` registers via a factory delegate that reads `OutboxProcessorOptions.BaseSeconds`/`CapSeconds` at first resolution, so the curve properties on the options class are live rather than decorative.

**Cancellation-token parameter name.** Contracts use `CancellationToken ct` rather than the design record's verbatim `CancellationToken cancellationToken`. The repo's existing `IEventStore` and `IEventStoreRepository` both use `ct`; matching the local convention beats faithfulness to the sketch.

**Static parameter-binding helpers duplicated in `OutboxProcessor`.** `AddBigInt`, `AddInteger`, `AddText`, `AddTimestampTz` are copied from `PostgresEventStore` into `OutboxProcessor` rather than promoted to an `internal static` shared file. Twenty lines of duplication is cheaper than a Session 0003 refactor whose scope wasn't approved; a future cleanup commit can consolidate if the SQL Server adapter session amplifies the duplication.

**One additional NuGet package not flagged in the Commit 4 proposal.** `Microsoft.Extensions.DependencyInjection` (the implementation package, version 10.0.0) was added to `Directory.Packages.props` and referenced from `Infrastructure.Tests` for `BuildServiceProvider()`. The proposal had only listed the abstractions package. Caught at first build of `ServiceCollectionExtensionsTests`.

## Internal notes

**`DateTimeOffset` introduced for time-policy work; `EventMetadata.OccurredUtc` remains `DateTime`.** `OutboxRetryPolicy.ComputeNextAttempt` takes and returns `DateTimeOffset` because `TimeProvider.GetUtcNow()` natively produces `DateTimeOffset`. `EventMetadata.OccurredUtc` stays `DateTime` (with `Kind = Utc`) because that's the existing repo convention and Chapter 8 doesn't commit either way. The processor converts at the SQL-parameter-binding boundary (`nextAttempt.UtcDateTime`). Not a flag against the book; a forward-looking note for whichever session decides whether the rest of the repo should normalize on `DateTimeOffset`.

## Notes for next sessions

**SQL Server adapter session (later in Phase 2).** Parallel hand-rolled relational implementation in `src/Infrastructure/EventStore.SqlServer/`. Self-contained per ADR 0004: its own `SqlServerEventStore`, `SqlServerOutboxProcessor`, `SqlServerOutboxRetryPolicy`, `ISqlConnectionFactory`, `AddSqlServerEventStore` extension. The PostgreSQL implementation from Sessions 0003 and 0004 is the structural template; the SQL Server side is not a shared layer. T-SQL specifics to translate: `OUTPUT INTO` instead of CTE-with-RETURNING for the quarantine move; `READPAST` + `UPDLOCK` instead of `FOR UPDATE SKIP LOCKED`; error 2627 for unique-violation translation on the `attempt_count` UPDATE path; filtered indexes (which SQL Server has) instead of partial indexes.

**Phase 6 (projections, Weeks 11-12).** The projection trigger session is where `LISTEN/NOTIFY` is properly designed and shipped. Same mechanism benefits the outbox processor's wake-up path. Decision then: does the LISTEN/NOTIFY signaler live in a shared component (against ADR 0004's per-adapter principle), or do the outbox processor and the projection trigger each grow their own LISTEN consumer (duplication but isolation), or does the projection trigger publish a NOTIFY that the outbox processor also listens on? Defer the answer; surface the question.

**`EventTypeRegistry` two-phase registration is awkward.** Cleaner shape — per-aggregate-module `IEventTypeProvider` contributions composed by a single registry factory — should land alongside the first host (Workers, probably with the Phase 2 Application command pipeline session).

**Composition-root extension exists but no host consumes it yet.** The `AddPostgresEventStore` extension ships in Session 0004 with its DI-resolution test, but no `Program.cs` calls it. First real consumer is whichever host lands first — Workers for the OutboxProcessor as a `BackgroundService`, or possibly Api/Web if the foundation plan shifts. The embedded migration-runner call flagged in Session 0002 (`MigrationRunner.RunPendingAsync` before binding ports) lands in that same host-introduction session.

**`OutboxRetryPolicy` curve is host-tunable as shipped.** `BaseSeconds = 1` and `CapSeconds = 300` are the policy's `public const int` defaults; `OutboxProcessorOptions` exposes both as `init`-able properties whose defaults reference the policy constants. The DI factory delegate constructs the policy from the options at first resolution, so a host that wants a different curve calls `services.Configure<OutboxProcessorOptions>` before `AddPostgresEventStore`. `MaxAttempts` is on the options too; it controls the processor's quarantine-branch decision rather than the curve.

**PLAN.md folder layout describes `Infrastructure/Outbox/` in its pre-ADR-0004 sense.** PLAN.md says the folder hosts "OutboxProcessor (PostgreSQL-resident outbox)". Per ADR 0004 the per-adapter `OutboxProcessor` lives in `EventStore.Postgres`, and Session 0004 repurposed `Infrastructure/Outbox/` for the engine-agnostic `InProcessMessageDispatcher`. Phase 14 reconciliation should update PLAN.md to describe the project's actual post-Session-0004 role; not blocking earlier work.

## Post-execution updates (F-0001-A, F-0001-E)

This section is the addendum the work order's planner-side discussion identified. It keeps the cross-repo flag-closure trail discoverable without taking the 0005 session number reserved for first-projection planning. F-0001-A and F-0001-E were both filed against Session 0004's PLAN.md and session_setup.md assumptions, so the closure record belongs in this log.

Commit `c82d568` ("Reconcile PLAN.md and session_setup.md against shipped state") closes both flags. The edits:

- **F-0001-A, PLAN.md Phase 1 port list.** `ISnapshotStore` and `IProjectionCheckpoint` were listed as Phase 1 ports. Neither shipped in Phase 1. Both moved to the Phase 1 Out-of-scope list with forward-pointers: `ISnapshotStore` to Phase 12, `IProjectionCheckpoint` to Phase 6, in PLAN.md's own phase numbering.
- **F-0001-E, PLAN.md Phase 3 event set.** Phase 3's Goals described an Order aggregate the shipped code does not implement, and the drift ran wider than the event list. Five bullets (lifecycle, events, command methods, invariant examples, value objects) were updated to the shipped Draft / AddLine / RemoveLine / SetShippingAddress / Place / Ship / Cancel lifecycle. The `OrderRepository` naming question and the Phase 3 phase-ordering divergence stayed out of scope and route to Phase 14.
- **session_setup.md Session 0004 status.** The doc described Session 0004 as "design committed, execution pending" across five sites. All five were corrected to shipped state. The "Planned test count" line and the "Cross-track flags pending" section were left as frozen-accurate, with the post-execution test count and the flag-index reconciliation routing to the Session 0005 forward refresh.

Book-repo follow-up: update the F-0001-A and F-0001-E rows in `cross-track-flags-summary.md` to resolved status, citing commit `c82d568`. That is a future book-repo session, not part of this addendum's commit.
