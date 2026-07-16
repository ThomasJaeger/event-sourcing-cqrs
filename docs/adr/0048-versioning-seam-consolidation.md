# 0048. Versioning Seam Consolidation

## Status

Accepted (July 2026)

## Context

ADR 0004 rules that event store adapters are self-contained: each carries its own
row construction and translation, and no cross-adapter layer exists for them to
share. The ADR accepts duplication as the cost and sets its own revisit trigger at
`docs/adr/0004-self-contained-event-store-adapters.md:30`: "If a future change
requires touching identical code in three or more adapters, the session making that
change evaluates whether to factor the shared concern at that point. Two-adapter
duplication is fine and expected under this ADR. Three-adapter duplication is the
threshold for reconsideration."

Phase 13's KurrentDB adapter landed the third copy of the type registries and the
JSON seam, and ADR 0047 recorded the trigger as fired. This ADR is the evaluation
step that wording calls for, taken before a fourth adapter lands rather than after.

**The trigger fired on a case ADR 0004 argued could not arise.** ADR 0004:32
reasons that "KurrentDB and DynamoDB do not have outboxes, so the relational outbox
abstraction would only become a three-consumer question if a third relational
adapter is added to the implementation. No such addition is currently planned." That
paragraph frames the shared concern as the outbox, and on the outbox it is still
right. The concern that tripped the trigger was the type registries and the JSON
seam, from a non-relational adapter, which is the path :32 argued away. ADR
0045:138-139 inherited the same framing ("A third relational adapter reopens ADR
0004's factoring question with the corrected cost figure"). The general trigger at
:30 governs and its threshold is met; the :32 analysis was scoped to one concern and
did not anticipate this one.

**The figures, measured from disk at this commit.** ADR 0045:103 records "roughly
234" duplicated lines "across the relational adapters." That figure reconciles
exactly, and it is right about what it measured. 234 is the SQL Server adapter's five
dedicated seam files: 77 + 77 + 24 + 27 + 29. It has been stable at 234 from a330f4a,
the commit that added ADR 0045, through HEAD. ADR 0045 is the SQL Server ADR and its
sentence says "the second adapter also duplicates," so a per-copy measure of that
adapter is what the number was always describing.

The correction this ADR makes is scope, not arithmetic. ADR 0047:138-143 cites the 234
as the figure the third copy is measured "against," which reads as a standing total for
the seam. It is not: it is one adapter's dedicated files, and it excludes even that
adapter's own 23-line inline JSON block. Measured whole, immediately before this
commit:

| Adapter | Dedicated files | JSON seam | Copy total |
|---|---|---|---|
| PostgreSQL | 238 (registries 86 + 77, converter 22, exception 24, metadata reader 29) | 24, inline in the composition root | 262 |
| SQL Server | 234 (77 + 77, 24, 27, 29) | 23, inline in the composition root | 257 |
| KurrentDB | 206 (78 + 78, 23, 27) | 26, dedicated file | 232 |

**751 lines across three copies**, where the figure in circulation was 234. The
executable bodies were identical across all three: diffing them with comments,
namespace, and line wrapping normalized away leaves nothing. The line-count spread is
commentary, so the counts overstate how much distinct code there ever was and
understate how mechanical the duplication is.

The lesson is about how a measured number travels. 234 was accurate, and it stayed
accurate for the adapter it measured. What went stale was its scope: it was written to
size one adapter's copy, then cited to size a three-copy question it never covered.

**The precedent for sharing already exists in the repository.** `CommandTypeRegistry`
sits in `src/Domain.Abstractions/CommandTypeRegistry.cs`, shared by all three
adapters, with the same two-dictionary, throw-on-duplicate, throw-on-unknown shape,
and its own header says it is built "so the three read alike." The registries'
justification for duplicating, that "no cross-adapter layer exists for them to sit
in," is contradicted by a registry of the same shape already sitting in one.

The registries' own comments have named `Infrastructure/Versioning` in Phase 15 as
the collapse point since the first copy. The directory has existed, tracked and
empty, since Phase 1.

## Decisions

**The mechanism is shared; the composition and the engine mechanics stay per
adapter.** `src/Infrastructure/Versioning` becomes a project holding one copy each of
`EventTypeRegistry`, `ProcessManagerEventTypeRegistry`, `TenantIdJsonConverter`,
`UnknownEventTypeException`, `EventMetadataReader`, and a single
`EventStoreJsonOptions.Create()` replacing two inline private factories and
`KurrentEventStoreJsonOptions`. It references Domain.Abstractions and nothing else,
so it carries no engine and no bounded context. Three copies collapse to one: 751
lines to 306.

**What does not move.** Each adapter keeps its own DI registration factories in its
own `ServiceCollectionExtensions`, with their existing TryAdd and Add postures
unchanged. The registration factories are near-identical across the adapters and
were not unified: they are composition, ADR 0004's subject, and the reason they look
alike is that they do the same wiring, not that they are one mechanism. Row
construction, translation, engine error mapping, and everything else ADR 0004 names
stay per adapter.

**ADR 0004 stays accepted with narrowed scope. It is not superseded.** Its rule now
reads: adapters are self-contained in their engine mechanics. Storage type naming and
the serialization shape are not engine mechanics, so they are outside that rule
rather than exceptions to it.

**The KurrentDB hydration coalesce keeps its own copy.** `KurrentEventHydration`
reads `StoredEventMetadata` over `ReadOnlyMemory<byte>` with the metadata nested a
level down; the shared `EventMetadataReader` reads `EventMetadata` over `string`.
Adopting the shared reader there would change what is read, not where the code lives,
so it was left alone and the difference is recorded here.

## Consequences

**The cross-engine JSON round-trip becomes structural.** A payload serialized on one
engine round-tripping on another depended on three copies agreeing on naming,
converters, and encoder. Nothing enforced that agreement; it held because three
authors copied carefully. It is now a property of there being one factory. The
encoder pin that `SqlServerOutboxEncodingTests` protects is pinned once rather than
three times, so relaxing it is one edit that fails one test, rather than one edit
that silently diverges two engines.

**One type widens; the other keeps its visibility.** The two types that were
`internal` in the adapters get different answers here, because they have different
consumers.

`EventMetadataReader` becomes `public`. Ten call sites in three adapter assemblies name
it directly: `PostgresEventStore`, `SqlServerEventStore`, both outbox processors, and
`PostgresCorrelationTraceReader`. Across an assembly boundary `internal` would hide it
from the adapters that call it, and granting three adapters friend access to their own
shared mechanism is worse than a public method with a documented contract.

`TenantIdJsonConverter` stays `internal`, with `InternalsVisibleTo` grants for
Infrastructure.Tests and Projections.Tests. No adapter names it: they reach it through
`EventStoreJsonOptions.Create()`, which is the whole point of the seam. Its only
out-of-assembly consumers are tests that construct it directly to build options matching
the stored shape. That is the same shape it had before the move, and consolidation is not
a reason to widen a type whose callers did not change.

The registries and `UnknownEventTypeException` were already `public` and stay so.

**Phase 15 opens on a project that exists.** The upcaster pipeline lands beside the
registries that name the storage types it reads, rather than landing at the same time
as a three-way collapse. `EventMetadataReader`'s tenant coalesce is a hand-written
tolerance for one field, which is what the pipeline generalizes.

**A fourth adapter writes no fourth copy.** The DynamoDB adapter references Versioning
and gets the registries, the exception, and the JSON shape. What it still owes is its
own composition, its own engine mechanics, and its own translation.

**One exception type across engines.** Each adapter previously raised its own
same-named `UnknownEventTypeException` from its own namespace, so a caller catching
one engine's type silently missed another's. There is now one type to catch.

## Rejected alternatives

**Land a fourth copy and collapse in Phase 15.** The trigger is an evaluation step,
and this is the evaluation. Deferring means the DynamoDB adapter is written against a
shape known to be leaving, its author copies 232 lines they will delete, and the
Phase 15 collapse becomes a four-way change landing beside the upcaster pipeline it
is supposed to make room for. The collapse is also easier to prove correct now: the
three copies are provably identical today, and the whole suite exercises them, which
is exactly the evidence a behavior-preserving refactor wants.

**Put the seam in Domain.Abstractions, beside CommandTypeRegistry.** It is where the
precedent sits, and it needs no new project. Rejected because Domain.Abstractions is
the ports layer, and these are not ports: they are a serialization mechanism with a
`JsonConverter` and a `JsonSerializerOptions` factory in them. Putting them there
would pull System.Text.Json into the layer the domain depends on and blur what
Domain.Abstractions is for. `CommandTypeRegistry`'s placement is the weaker precedent
here rather than the model to follow; whether it should move to Versioning is a
question this ADR does not settle.

**Leave the JSON seam per adapter and share only the registries.** Rejected because
the JSON seam is the part where duplication is most dangerous. The registries diverging
produces a loud unknown-type failure. The options diverging produces silent
cross-engine corruption, which is the failure ADR 0045's encoder pin exists to
prevent and the reason the pin's own comment says three copies is "how an options pin
gets applied to two of them and missed on the third."

**Make `TenantIdJsonConverter` public for uniformity with the rest of the project.**
One visibility across every type in Versioning reads tidier and needs no friend grants.
Rejected because tidiness is not a reason to publish a type. The converter is an
implementation detail of `EventStoreJsonOptions.Create()`, and publishing it invites a
caller to construct options by hand from the parts rather than through the seam, which
is the divergence this ADR exists to close.

**Route the tests through `EventStoreJsonOptions.Create()` and keep the converter
unreachable.** No friend grants at all, and the tests would exercise the shipping seam.
Rejected because the tests that name the converter build options deliberately unlike the
adapter's, to pin a stored shape against a different naming policy. Handing them the
factory would erase the difference each is testing. The friend grant costs two lines and
keeps those tests honest.

## Trigger for revisiting

If an engine needs a storage type name or a serialization shape that differs from
every other engine's, this consolidation is wrong for that engine and the seam needs
a per-engine seam rather than a shared one. That would be a real finding about the
abstraction, not a reason to copy the file: the cross-engine round-trip is a property
this repository claims, and an engine that cannot hold it should surface rather than
diverge quietly.

If Phase 15's upcaster pipeline needs the registries to carry version information
they do not carry today, the shape here changes with it. This ADR fixes where the
mechanism lives, not what it will grow into.
