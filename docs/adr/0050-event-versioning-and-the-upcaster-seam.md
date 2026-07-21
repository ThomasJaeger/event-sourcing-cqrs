# 0050. Event Versioning and the Upcaster Seam

## Status

Accepted (July 2026)

## Context

Phase 15's first arc is event schema versioning, the Chapter 11 pattern: an event's
stored shape changes over time, and a reader has to lift an old shape to the current
one without mutating what history holds. ADR 0048 collapsed the type registries and
the JSON seam into `Infrastructure/Versioning` ahead of this arc, so the upcaster
pipeline lands beside the registries that name the storage types it reads. This ADR
records the decisions the arc made, from the pipeline's first RED through the
OrderDrafted worked example and the write-stamp repair that example exposed. The
pattern is real and running: four engines resolve a stored version to a CLR type,
lift it, and stamp the current version, and one bounded context (Sales, OrderDrafted)
carries a live two-version lineage.

## Decisions

**EventVersion on the envelope is the canonical schema-version carrier.**
`EventEnvelope` carries `EventVersion` as a top-level field, and it is the schema
version a reader trusts. Every engine writes it where a read can project it before
touching the payload:

| Engine | Where EventVersion lands |
|---|---|
| PostgreSQL | `event_version` SMALLINT column |
| SQL Server | `event_version` SMALLINT column |
| DynamoDB | top-level number attribute |
| KurrentDB | field on the stored-metadata envelope |

Resolution needs the version before the payload can be deserialized, because the
version is what chooses the CLR type to deserialize into, so the carrier has to be
readable first. `EventMetadata` also carries a `SchemaVersion` field, written 1
everywhere and read by nothing on the resolution path. It is recorded here as a
hazard rather than removed: it is inert while it reads 1, and it is a second place a
version could be written and a future author could believe. Its disposition is
deferred. Rejected: `SchemaVersion` as the canonical carrier, because it lives inside
the metadata JSON, which a resolver would have to crack before it could read the
payload, and on KurrentDB it is nested a level down inside the stored-metadata
envelope, so the one field the read path needs first would be the one buried deepest.
Rejected: unifying the two by deleting `SchemaVersion` now, because that is a
four-engine stored-shape migration over every historical row for a field that cannot
drift while it reads 1, spending a migration's risk to tidy a field that is inert.

**Versions are derived from chain topology, never declared.** An upcaster
registration is a link from one shape to the next and carries no number; the pipeline
orders the links into a chain per storage name and reads the version off the
position, so version 1 is the oldest shape and the registered terminal is the current
version. Construction validates the shape of the link set loudly: a gap below the
terminal, an orphan that reaches no registered type, a duplicated link, a branch or a
merge that makes the graph non-linear, and a cycle each throw at composition time,
before any event is read. Rejected: declared `FromVersion` and `ToVersion` on each
registration, which reads more explicit and needs no topology walk. A declared number
is a number a human types, and a mis-numbered registration compiles and corrupts
resolution in silence: two links both claiming version 2, or a jump from 1 to 3,
deserialize a stored row into the wrong shape with no error. Positional derivation has
no number to get wrong, so the same defect surfaces as a gap or a duplicate the
constructor rejects.

**The read-time pipeline lives in `Infrastructure/Versioning`; stored events never
mutate; identity returns at the current version.** A read resolves the stored version
to its CLR type, deserializes into that type, and lifts it forward through the chain,
so an old row on disk stays an old row and the lifting happens in memory on the way
out. At the current version there is nothing to lift, so the same instance returns.
The upcaster contract, `Upcaster<TFrom, TTo>` and the `IEventUpcaster` marker, lives
in Domain.Abstractions beside the events it versions, because a domain event's
upcaster is a domain concern and must reference only domain types. The pipeline binds
a compiled delegate to the base's internal dispatch bridge across the assembly
boundary, so Domain.Abstractions grants `InternalsVisibleTo` to Versioning. The plan's
"schema registry" resolved as the existing `EventTypeRegistry` plus the lineage
knowledge the pipeline derives at construction, not a new type: the registry maps
storage name to the terminal CLR type, and the pipeline walks the chain, so the
registry a version resolver needs is the two together. Rejected: the contract in
Versioning with Domain referencing it, which is where the pipeline sits and needs no
friend grant. It would pull an infrastructure dependency, and through it
System.Text.Json, into the domain layer, the same boundary ADR 0048 refused when it
kept the JSON seam out of Domain.Abstractions. Rejected: a provider abstraction (an
`IUpcasterProvider` parallel to `IEventTypeProvider`) before a second bounded context
has a batch of upcasters to register; one upcaster registers directly as a singleton,
and a provider waits for the batch that would justify it. Rejected: a third registry
type dedicated to versions, which duplicates what `EventTypeRegistry` already knows
about storage names and what the pipeline already derives about chains.

**The DI seam is the `IEventUpcaster` marker, enumerated at five composition roots,
threaded as a required dependency.** A composition root resolves
`sp.GetServices<IEventUpcaster>()` and hands the set to the pipeline, the same shape
by which it enumerates `IEventTypeProvider` to build the registry. The five roots are
the four engine `Add*EventStore` extensions, with PostgreSQL registering in both its
store root and its replay-reader root. The pipeline is a required constructor
dependency of every store and every outbox processor, and the pipeline is never
null-tolerant. Rejected: an optional pipeline parameter defaulting to a no-op, which
lets a store compose without it and read a stored old shape as its current type with
no lift and no error, a silent hole that reads correct until an event gains a lineage.
A required dependency turns that into a composition-time resolution failure. Rejected:
assembly scanning for upcaster types, which composes by reflection over whatever
happens to be loaded rather than over what a host explicitly registered, and hides the
registration list a reader of the composition root can otherwise see.

**The outbox row carries `event_version`, and a dispatched message stamps the current
version.** Threading the pipeline through the dispatch path found the outbox had no
version to carry, which was the STOP that split the carrier into its own migration
ahead of the composition work. A dispatched message resolves through the pipeline like
a read does, so it needs the stored version to resolve the shape and it stamps the
current version onto the `OutboxMessage` it hands the dispatcher. The relational outbox
and quarantine tables gained the column with a default of 1, so every historical row
reads as the version it was written at.

**The write path stamps `ICurrentEventSchemaVersions.CurrentVersionFor`, not a
literal.** `EventStoreRepository` asks the port for the current version of each event's
storage name and stamps that, replacing a literal 1 that was correct only while no type
had a lineage. The literal's defect is the motivating one and is recorded here: once
OrderDrafted's current version became 2, a freshly written order was stored at version
1, so the read took its current-shape payload for a version-1 row, resolved it as the
old shape, and lifted it, replacing the real channel with the upcaster's default. A
round-trip fact caught it. `EventUpcasterPipeline` implements the port from the same
chain topology it resolves reads with, so the version a write stamps and the version a
read expects come from one place and cannot disagree. `ProcessManagerRepository` keeps
its own literal 1, correct while no process-manager event has a lineage, and it is
named here as the twin edit the first PM upcaster forces: the same port, the same
stamp, on the PM write path.

## Consequences

- Four engines resolve stored versions and lift old shapes through one pipeline, and
  one bounded context (Sales) carries a live lineage, OrderDrafted version 1 to version
  2, pinned at the default engine as the Chapter 11 worked example.
- The write path and the read path derive the current version from one topology, so a
  write stamps what a read expects. A literal on either side is the defect this arc
  closed on the aggregate path and left standing, named, on the PM path.
- The upcaster contract in Domain.Abstractions lets a domain lineage reference only
  domain types, while the pipeline that composes and resolves stays in infrastructure,
  reached across a friend grant.
- `EventVersion` is the one carrier the resolution path reads.
  `EventMetadata.SchemaVersion` is a dormant second place a version could live, inert
  at 1, and its removal is a deferred four-engine migration rather than a cleanup.
- A required pipeline dependency means an engine composed without upcasters resolves a
  pipeline with an empty chain, so every event is already at version 1 and the read
  path is unchanged, rather than a store composed with no pipeline at all.

## Trigger for revisiting

A second bounded context registering a batch of upcasters reopens the provider
question. One upcaster registers directly today, and a batch is the point at which an
`IUpcasterProvider` parallel to `IEventTypeProvider` earns its abstraction.

A process-manager lineage forces the PM pipeline and the PM write stamp:
`ProcessManagerRepository`'s literal 1 becomes the port call, and the PM read path
gains the resolution the aggregate read path already has.

An engine that needs a version carrier its peers do not, a per-engine column shape or
a metadata-nested version the others cannot read first, reopens the canonical-carrier
decision. That would be a finding about the engine rather than a reason to fork the
field.

`EventMetadata.SchemaVersion`'s disposition reopens the moment it is written anything
but 1, the point at which the deferred hazard becomes a live second carrier and the
delete-versus-keep question has to be answered.
