# 0046. Non-Relational Companion Port Posture

## Status

Accepted (July 2026)

## Context

The Option A ruling of the Phase 2 SQL Server arc (session 0050) scoped the
companion ports, IIdempotencyStore and IDelayQueue, to the relational adapters:
each ships its own implementation against its own database, IEventStore gains no
methods, and what a non-relational engine supplies for those ports stayed open
per ADR 0017. Phase 13's KurrentDB host is the first non-relational host to hit
that open question, and it hits it immediately: IdempotencyBehavior is registered
unconditionally for every command, so a host that leaves IIdempotencyStore
unregistered fails at its first command, before any append. This ADR records the
answer that ruling deferred.

## Decisions

**The companions stay on PostgreSQL, on the read-model database.** A
non-relational event-store host composes IIdempotencyStore and IDelayQueue as
their PostgreSQL implementations against its read-model database, which is
PostgreSQL on every provider. The command_idempotency and delayed_commands tables
already live in that database's schema (migrations 0007/0019 and 0008/0020), and
the projections the host runs already read it, so the companion ports need no new
database and no new migration. KurrentDB holds the events; PostgreSQL holds the
idempotency keys and the scheduled commands. IEventStore still gains no methods,
so the boundary the Option A ruling drew holds across the relational/non-relational
line rather than only within the relational adapters.

**Standalone connection-string registrations carry it.** The relational adapters
bundle the companion ports inside AddPostgresEventStore, which owns the event
store's data source. A non-relational host cannot use that path, so the
companions register through connection-string overloads that own their own
container-scoped connection seam: AddPostgresIdempotencyStore(connectionString),
AddPostgresDelayQueue(connectionString), and AddPostgresDelayQueueProcessor(
connectionString), each all-TryAdd so they compose beside the read-model
registration against the same database without double-registering it. The
delay-queue registrations capture the Postgres serializer shape and hand it to
the store rather than resolving the container's JsonSerializerOptions, which on a
non-relational host is the event-store adapter's own shape; a scheduled command
must round-trip through the read-model schema's shape, not that one, and an
adapter test pins the distinction.

**The host composes only the companions its command pipeline reaches.** The Api
host, which dispatches commands over HTTP and drains nothing, composes only
AddPostgresIdempotencyStore: its sole IDelayQueue consumer is the OrderFulfillment
process-manager handler, which lives in the ProcessManagers assembly AddApplication
does not scan, so the Api host never registers it and needs no delay queue. The
Workers host, which runs the process managers, composes all three, with the
delay-queue processor after AddApplication so its ICausedCommandBus dependency
resolves. The AdminConsole is read-only and composes no companion at all.

**Native scheduling is deferred, not adopted.** ADR 0017's revisit trigger expects
KurrentDB's native scheduled messages as an adapter-shape change behind IDelayQueue
rather than a superseding event. This posture takes none of that shape: it reuses
the relational delay queue on PostgreSQL rather than building a KurrentDB-native
scheduler. The reuse is the cheaper honesty while one non-relational engine ships;
a native scheduler would be its own implementation and its own ADR.

## Consequences

- A KurrentDB or DynamoDB host's idempotency and timeout state lives in a
  different database than its events. Provider selection stays a deployment-time
  choice for a fresh database set, not a runtime swap, consistent with ADR 0045's
  configuration consequence: the companions' PostgreSQL database and the event
  store's KurrentDB node are provisioned together.
- The connection-string companion extensions are a second registration surface
  for the same ports, beside the bundled relational one. Both are exercised: the
  bundled path by the relational hosts, the standalone path by the non-relational
  hosts, each with its own resolution and drain tests.
- The delay-queue processor runs its poll loop against the read-model PostgreSQL
  database on a KurrentDB host, so process-manager timeouts drain there while the
  events flow through KurrentDB's subscription. The two mechanisms do not share a
  connection or a transaction.

## Trigger for revisiting

A non-relational deployment that has no PostgreSQL read-model database to lean on
reopens this, as does a decision to adopt an engine-native scheduler (KurrentDB
scheduled messages, DynamoDB TTL) in place of the reused delay queue. DynamoDB's
arc inherits this posture as its starting answer and rules whether it holds for a
second non-relational engine.
