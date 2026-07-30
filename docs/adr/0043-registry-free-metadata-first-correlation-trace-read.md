# 0043. Registry-Free, Metadata-First Correlation Trace Read

## Status

Accepted (July 2026). Resolves the mixed-row read shape that ADR 0013 reserved for the Phase 12 Correlation-ID Tracer. Refines ADR 0013's per-row-routing reservation and depends on ADR 0042, which made process-manager rows carry a live correlation in the first place.

## Context

The Correlation-ID Tracer answers one operator question: what happened under this correlation id. The answer spans aggregate rows and process-manager rows, which share one physical table, `event_store.events`, per ADR 0013. ADR 0013's Consequences promise that "PM rows appear in the trace results alongside aggregate rows, joined by `correlation_id` on `EventMetadata`. No two-table query needed." Migration 0001 built for this consumer by name: `correlation_id` is a STORED generated column projected out of the metadata JSONB, and `ix_events_correlation_id` indexes it.

The obstacle is payload typing, not storage. The two event families are disjoint at the type level. `EventEnvelope` carries an `IDomainEvent` payload and `ProcessManagerEventEnvelope` carries an `IProcessManagerEvent` payload, both sealed records with no common base and no shared interface. They resolve through separate registries, `EventTypeRegistry` and `ProcessManagerEventTypeRegistry`, and the store selects between them by which typed read method runs. `TypeFor` throws `UnknownEventTypeException` on a miss, on both registries, with no discriminator saying which registry missed and no Try-style lookup anywhere in the tree. The AdminConsole composes four `IEventTypeProvider` registrations and zero `IProcessManagerEventTypeProvider` registrations, so its PM registry resolves empty, and the ProcessManagers project is not in its reference closure.

So any read that hydrates payloads has to answer "which registry" per row, and a wrong answer throws. ADR 0013 line 19 named this and deferred it: "The case that needs per-row routing, a single query disambiguating PM and aggregate rows in one pass, is the Phase 12 Correlation-ID Tracer's."

The reservation assumed the Tracer would hydrate. It does not have to. An operator reading a trace wants to see what the store holds, and the payload is already JSON on disk.

## Decision

`ICorrelationTraceReader` is a new port in `Domain.Abstractions`, on the `IEventStoreHeadPosition` precedent, implemented by `PostgresCorrelationTraceReader` in the Postgres adapter. It returns `CorrelationTraceResult`, carrying `CorrelationTraceRow` values and a truncation flag.

**The read is registry-free and metadata-first.** It resolves no CLR payload type, consumes neither event-type registry, and hands the payload back as the JSON text the store holds. Metadata deserializes into a typed `EventMetadata` through `EventMetadataReader`, the tolerant seam every other read in the adapter uses. Because no payload type is resolved, the question "which registry" never gets asked, and one query serves both event families. The AdminConsole gains no ProcessManagers reference and registers no PM providers.

**One query, cap-bounded.** A single `SELECT` on `event_store.events` filters `correlation_id = @correlation_id`, orders ascending by `global_position`, and fetches `maxRows + 1` rows. At most `maxRows` materialize. The extra row is counted, dropped, and never returned; its presence sets `Truncated`. That is one index scan and no count query. The cap is a parameter on the port, so the port executes the bound it is handed; the seam owns the constant.

**The row record carries every real column of `event_store.events`.** The three generated columns (`correlation_id`, `causation_id`, `tenant_id`) are omitted because they are pure projections of the metadata JSONB and the typed `EventMetadata` already carries their values. `occurred_utc` is carried, because it is a real column written independently of the metadata blob on every append path. A forensic read reports the column the store holds rather than a value re-derived from a different column, so a divergence between the two is visible if one ever exists.

**Per-row routing lands as prefix classification, seam-side.** ADR 0013 reserved per-row routing for this slice and the reservation is now refined. Routing by registry proved unnecessary the moment no hydration occurs. What the operator still needs is to know which family a row belongs to, and the `pm-` stream-id prefix answers that with a string test, the same convention `ReadAllAsync` filters with and the Event Store Browser pre-detects with. Classification is display, so it belongs to the seam, not the port. ADR 0013's one-table join holds exactly as written.

**Metadata failure fails the whole read.** A metadata deserialize that throws surfaces. There is no per-row tolerance and no catch, and the page renders its error state.

**`Guid.Empty` is refused at the seam.** The port accepts it, since it executes what it is given. The seam classifies it as `InvalidFormat`.

## The conformance-signal trade-off

This is the real cost of the decision and it is worth stating plainly.

The Event Store Browser deserializes each payload against its registered CLR type and re-serializes it for display. That round trip is a conformance check that the Browser gets for free: a stored payload that no longer binds to the current type fails loudly there. This read forfeits that signal. A payload whose stored shape has drifted from its current type renders here without complaint, because nothing binds it to a type.

That is the right trade for this tool and the wrong one for the Browser, so the two keep different postures. Conformance checking stays the Browser's job, where an operator inspects one stream against current types. The Tracer's job is to show what is on disk across a workflow, and a trace can legitimately span schema epochs, where old rows in their old shapes are the truth being reported rather than a defect being hidden.

## The two-posture asymmetry

Payload non-conformance renders. Metadata non-conformance throws. The postures differ because the two columns are different kinds of thing.

Payload is versioned by design. `SchemaVersion` exists on the metadata, Chapter 11 teaches upcasting, and an old payload shape is a legitimate thing to find in an event store. Rendering it is correct.

Metadata is the platform's own fixed shape. Every row the system has ever written carries the same metadata contract, and the one tolerated variation, a pre-tenancy row with no tenant, is already handled inside `EventMetadataReader`. There is no legitimate old form beyond that. A metadata blob that will not deserialize is a defect, so the read fails and the operator sees the error rather than a page of half-rendered rows.

## Rejected alternatives

**Prefix-routed dual-registry hydration.** Route each row by its `pm-` prefix to the matching registry and hydrate the payload. Rejected on three counts. It needs a `TryTypeFor` that does not exist on either registry, so either a new API on both or catch-driven control flow on an exception type that does not say which registry missed. It forces a ProcessManagers project reference into the AdminConsole so the host can register PM providers, widening a host that ADR 0040 deliberately kept to a focused read composition. And it buys nothing: the hydrated object's only use here is to be serialized straight back to JSON for display, so the round trip purchases re-serialization and a new failure mode.

**Composing PM providers with a display-level union.** Register the PM providers in the AdminConsole and either run two queries, one per family, or run one query and split the rows into two typed collections. Two queries break ADR 0013's one-table-join promise. One query into two collections inherits the same registry coupling and the same project reference, and still has to reunite the two collections in `global_position` order for display.

**A common envelope base, or an envelope with an `object` payload.** Give `EventEnvelope` and `ProcessManagerEventEnvelope` a shared supertype so one list can hold both. Rejected because it restructures two core types in `Domain.Abstractions` to serve one read tool. The disjointness is deliberate (ADR 0012), it is load-bearing for the typed read methods, and a display concern is a poor reason to weaken it.

**`ReadAllAsync` with in-memory filtering.** Stream the global feed and keep the rows whose correlation matches. Rejected on all three axes. It is an unbounded scan of the whole table to find a handful of rows, it excludes `pm-` streams by SQL predicate so it cannot see PM rows at all, and it would throw on the AdminConsole's empty PM registry if that filter were lifted. It also ignores the index that migration 0001 built for this exact consumer.

**Per-row metadata degradation.** Catch a metadata deserialize failure, render the row with whatever survived, and flag it. Rejected because no producer for the failure class exists: every write path stamps metadata through one type. Silent partial rendering would hide the finding the tool exists to surface, and a test for it would have to inject corruption below the system's own invariants, which pins a behavior the system cannot produce. Tolerance is born at the consumer if a future writer ever makes the class real.

## Consequences

* One query on an indexed column answers a whole trace, across both event families and across tenants. ADR 0013's Consequences hold as written.
* The AdminConsole reads process-manager rows without referencing ProcessManagers and without registering a single PM event type. Its focused read composition under ADR 0040 is unchanged.
* The Tracer is the first bounded read of `event_store.events` in the tree. Every other read of that table is unbounded.
* Payload text is PostgreSQL's canonical rendering of the JSONB document rather than the bytes the serializer emitted. Keys come back ordered by the engine and whitespace is normalized. The keys, the values, and a decimal's scale all survive. An operator comparing a Tracer payload against a Browser payload byte for byte will see different formatting for the same document.
* A trace of `Guid.Empty` is unreachable through the seam. Process-manager rows written before ADR 0042 carry an empty correlation, so a workflow spanning that epoch shows its aggregate rows with the PM legs absent. That discontinuity is the one ADR 0042 reports as a fact about the system's history, and the Tracer reports it the same way.

## Trigger for revisiting

Two events reopen this.

A consumer that needs typed payloads out of a trace, rather than text for display. Nothing in Phase 12 does, and a consumer that did would be doing work the Browser already covers.

A legitimate metadata schema evolution. The fail-closed metadata posture holds because metadata has one shape and one tolerated legacy form. A second real form would make per-row metadata tolerance a question worth asking again, and the answer would be born at the consumer that needs it.

## Amendment (July 2026)

The rendering endorsement above is overtaken. Its premise was that `SchemaVersion` was the
event's version, which it never became: ADR 0050 shipped the version as
`EventEnvelope.EventVersion`, derived from chain topology, and left the metadata field inert
at 1. The field is removed as of Phase 17.

The browser already rendered the live version. `EventStoreBrowser.razor` shows
`schema @evt.EventVersion` on every event row unconditionally, while the metadata detail
block showed the inert field under the label "Schema version" only for an event whose
metadata an operator had expanded. The second render was a strict subset of the first, so no
operator ever saw the inert value without the live one already above it.

The row is deleted with no replacement. The live version stays on the row header where it
always was, and the metadata block no longer offers a second number that reads like a version
and is not one.
