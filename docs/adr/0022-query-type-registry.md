# 0022. Query Type Registry

## Status

Accepted (May 2026)

## Context

Phase 7's `/queries` HTTP endpoint dispatches by a type discriminator carried in
the request envelope (`{type, payload}`). The endpoint reads the `type` token,
resolves it to a CLR query type, deserializes the payload against that type, and
dispatches the resulting `IQuery<TResult>` through `IQueryBus`. That resolution
needs a query-type-token-to-Type map. No such map exists on disk: queries have
been in-process only until Phase 7, dispatched by their compile-time type through
`IQueryBus`, so nothing ever needed to name a query by a string token.

Three type registries already exist, all in
`src/Infrastructure/EventStore.Postgres/`: `EventTypeRegistry` (aggregate events,
from the initial event-store implementation), `ProcessManagerEventTypeRegistry`
(PM events, ADR 0013), and `CommandTypeRegistry` (scheduled commands, ADR 0017).
They share one shape: two ordinal dictionaries (name-to-type and type-to-name),
fluent `Register` overloads, throw-on-duplicate and throw-on-unknown,
`NameFor`/`TypeFor` lookups, populated from per-context pull-providers walked at
lazy build through `TryAddSingleton` plus `GetServices`. The query registry needs
that same shape.

The three precedents share a concern the query registry does not. Each exists to
round-trip a persisted storage-side type name back to a CLR type:
`EventTypeRegistry` resolves the `event_type` column on read,
`ProcessManagerEventTypeRegistry` resolves PM event rows, and
`CommandTypeRegistry` resolves the `command_type` column on a delay-queue row
(ADR 0017). They live in `EventStore.Postgres` because their consumers (the
Postgres event store, the PM read path, the delay queue) live there, and because
they are a persistence concern. The `EventTypeRegistry`'s own comment records that
this home is provisional: "Slated to move to Infrastructure/Versioning when that
project is created in Phase 12 ... lives here for now because the PostgreSQL
adapter is the only consumer."

The query registry round-trips an HTTP-transport-side type discriminator back to
a CLR type. Its concern is transport, not persistence. Its consumer is the
`/queries` endpoint in `Hosts.Api`, not any event-store adapter, and queries never
persist, so no storage-side name is ever written. The structural shape parallels
the three precedents; the architectural placement diverges deliberately.

The constraint: give the query registry the same pull-provider shape and lazy-walk
mechanism the precedents established, at the architectural home a transport concern
belongs in rather than the historical home the persistence registries occupy.

## Decision

A `QueryTypeRegistry` maps query type tokens to CLR types for HTTP dispatch,
populated from per-context `IQueryTypeProvider` implementations.

Placement:

- `QueryTypeRegistry` lands in `src/Application/`, alongside `QueryBus.cs`, the
  query-dispatch infrastructure at the Application root. Queries are an Application
  concern, and the registry that names them for transport belongs with the query
  dispatch it serves.
- `IQueryTypeProvider` lands in `Domain.Abstractions`, alongside the three existing
  provider interfaces (`IEventTypeProvider`, `IProcessManagerEventTypeProvider`,
  `ICommandTypeProvider`). The abstraction stays independent of any concrete
  registry, matching the precedents.
- `SalesQueryTypeProvider` and `FulfillmentQueryTypeProvider` land in
  `src/Application/Queries/Sales/` and `src/Application/Queries/Fulfillment/`,
  alongside the query types they own. Sales owns `ListOrders`, `GetOrderDetail`,
  and `GetCustomerSummary`; Fulfillment owns `GetAllInventoryDashboard` and
  `GetInventoryDashboardBySku`. Billing and Customer Support ship no queries and
  need no providers.

Walk site: `AddApplication` walks `sp.GetServices<IQueryTypeProvider>()` lazily at
first resolution and calls `registry.Register(queryType)` for each declared type,
registered with `TryAddSingleton` so a host can pre-register a fully populated
registry and win. This is the same lazy-build mechanism `AddPostgresEventStore`
uses for the three existing registries, relocated to the Application composition
extension because the query registry is an Application concern.

Inherited shape: two ordinal dictionaries (`_byName` with `StringComparer.Ordinal`,
`_byType`), throw-on-duplicate in both directions, throw-on-unknown through a new
`UnknownQueryTypeException`, and the `NameFor(Type)` / `TypeFor(string)` lookups
the precedents expose.

Three deliberate differences from the precedents:

1. Different home and walk site. The registry lands in `src/Application/` and is
   walked in `AddApplication`, not in `EventStore.Postgres` and
   `AddPostgresEventStore`. The setup doc projected `src/Application/QueryTypeRegistry.cs`
   but justified it as "paralleling the existing three registries," a parallel that
   would have argued for the `EventStore.Postgres` home the three occupy. The layer
   the setup doc proposed (Application) is correct; the reasoning here
   (transport-not-persistence) replaces the parallel-to-existing-three reasoning the
   setup doc carried.
2. Different type-family guard. The three registries constrain their generic
   `Register<T>()` overloads on a non-generic marker (`IDomainEvent`,
   `IProcessManagerEvent`, `ICommand`) and guard the non-generic `Register(Type)`
   path with `IsAssignableFrom` on that marker. `IQuery<TResult>` is generic with
   no non-generic marker, so the query registry's guard is a reflection check that
   the type implements `IQuery<>` for some `TResult`, not an `IsAssignableFrom` on a
   single marker type. This is the one place the parallel is shape-only.
3. An added enumerate method. The three precedents expose only `NameFor`/`TypeFor`;
   nothing enumerates a registry's full contents, because no persistence consumer
   needs the catalog. The `GET /queries` introspection endpoint (Cluster 2 Commit
   11) needs the registered query catalog, so `QueryTypeRegistry` adds an
   enumeration method the precedents lack. Commit 6 finalizes its exact name and
   return shape.

Provider placement correction: the setup doc projected the providers at
`src/Domain/Sales/SalesQueryTypeProvider.cs` and
`src/Domain/Fulfillment/FulfillmentQueryTypeProvider.cs`, parallel to the event
providers' `src/Domain/{Context}/` home. The hexagonal layering rule forbids this.
A query provider must reference the query types it owns, and those live in
`src/Application/Queries/`; Domain has no outward dependencies, so a Domain-resident
provider cannot name an Application type. The event providers can live in Domain
only because events are Domain types. Query types are Application types, so their
providers live in `src/Application/Queries/{Context}/`.

Not committed here: query handler registration stays as it is. `AddApplication`
assembly-scans the Application assembly for `IQueryHandler<,>` and registers each.
The type registry and the handler scan are independent mechanisms that both engage
in `/queries` dispatch: the registry resolves the token to a query type, and the
scan-registered handler resolves the query type to its handler. This ADR does not
touch the scan; the existing assembly-scan registration in `AddApplication`
continues unchanged, and the query registry is additive to it rather than replacing
it.

Alternatives rejected: a single unified registry handling token-to-Type round-trips
for events, PM events, commands, and queries together. The precedents already
rejected this implicitly. ADR 0013 chose "a second registry rather than extending
the single one," and ADR 0017 added a third in the same shape rather than merging.
A unified registry would couple four type families of different concerns (three
persistence, one transport) across different layers behind one type. The
repository's pattern is one registry per family, each parallel in shape but placed
by its concern.

## Consequences

- Ships: `IQueryTypeProvider` in `Domain.Abstractions`; `QueryTypeRegistry` and the
  `AddApplication` walk in `src/Application/`; `SalesQueryTypeProvider` and
  `FulfillmentQueryTypeProvider` in `src/Application/Queries/{Context}/`;
  `UnknownQueryTypeException`. The code lands in Cluster 1 Commit 6; this ADR commits
  the design.
- The placement asymmetry between this registry (`Application`) and the three
  existing registries (`EventStore.Postgres`) is intentional and documented. The
  query registry's Application home is correct from day one. The three existing
  registries' `EventStore.Postgres` home is historical: per the `EventTypeRegistry`
  on-disk comment, they are slated to move to `Infrastructure/Versioning` when that
  project is created in Phase 12. After that migration the four registries sit in
  two places by concern (persistence registries in Versioning, the transport
  registry in Application), not in one place by habit.
- Forward consumers: Cluster 2 Commit 10's `POST /queries` endpoint resolves the
  envelope's `type` token through `QueryTypeRegistry.TypeFor`; Cluster 2 Commit 11's
  `GET /queries` introspection endpoint reads the catalog through the new enumerate
  method.
- Forward finding for `CommandTypeRegistry`: the parallel `GET /commands`
  introspection endpoint needs to enumerate the command catalog, but
  `CommandTypeRegistry` exposes only `NameFor`/`TypeFor`, with no enumerate. Cluster
  2 Commit 11 surfaces the choice: add an `EnumerateCommands` method to
  `CommandTypeRegistry`, or surface the command catalog by another route. Not decided
  here.
- F-0011 candidate against Chapter 15's query-dispatch depiction: the chapter shows
  per-query endpoints; this ADR's `/queries` envelope with a type discriminator
  diverges. Session 0011 close transcribes.

## Trigger for revisiting

- A query consumer needs by-name serialization for persistence. None is anticipated:
  queries do not persist. If one arises (a cached query result keyed by query
  identity, say), the transport-only framing widens and the persistence concern the
  three precedents carry would apply to queries too.
- The Phase 12 `Infrastructure/Versioning` migration consolidates the type
  registries. If that project's framing claims type registries as a Versioning
  concern broadly, the query registry's Application placement is re-examined against
  it. The transport-not-persistence distinction is the argument for keeping the query
  registry in Application even then; this trigger keeps the decision reversible if the
  Versioning framing turns out to subsume it.
- Query handler registration moves from assembly scan to explicit registration. That
  is a different mechanism from the type registry and out of this ADR's scope; it
  would be its own ADR.
