# 0023. User Command Registration in CommandTypeRegistry

## Status

Accepted (May 2026)

## Context

`CommandTypeRegistry` (ADR 0017) maps command type names to CLR types and back.
It exists for the delay queue: a scheduled command is stored by command_type and
resolved back to a concrete ICommand on dispatch. The only ICommandTypeProvider
on disk is OrderFulfillmentCommandTypeProvider, which registers the two timeout
commands the delay queue round-trips. The user-dispatched commands (DraftOrder,
CancelOrder, and the rest) have never been registered by name: they dispatch
in-process through ICommandBus by their compile-time type.

Phase 7's POST /commands endpoint dispatches a user command by a type
discriminator in the request envelope, so it needs the user commands resolvable
by name. They are registered nowhere, so CommandTypeRegistry.TypeFor("CancelOrder")
throws UnknownCommandTypeException. This is the command-side twin of the gap
ADR 0022 closed for queries.

The placement question parallels ADR 0022. For queries, no registry existed, so
ADR 0022 created QueryTypeRegistry in Application because query-by-name dispatch
is a transport concern, not the persistence concern the event-store registries
serve. For commands, a registry already exists (CommandTypeRegistry, in
EventStore.Postgres for the delay queue). The choice is to reuse it or to add a
transport-specific command registry mirroring QueryTypeRegistry.

## Decision

Reuse the existing CommandTypeRegistry. Add per-context user-command providers
that feed it through the existing ICommandTypeProvider interface and the existing
AddPostgresEventStore walk:

- SalesCommandTypeProvider, FulfillmentCommandTypeProvider, and
  BillingCommandTypeProvider in src/Application/Commands/{Context}/, beside the
  command types they own. Placement follows the ADR 0022 layering rule: command
  types are Application types, so their providers live in Application, paralleling
  the query providers in Application/Queries/. The event providers live in Domain
  only because events are Domain types.
- The Api host registers the three providers; AddPostgresEventStore's
  ICommandTypeProvider walk populates the registry on first resolution. The
  Workers host keeps only OrderFulfillmentCommandTypeProvider.

Reuse rather than a transport twin. CommandTypeRegistry is a concern-agnostic
name-to-type map; the delay queue consumes it for a persistence round-trip and
/commands for a transport round-trip. ADR 0022 created a new registry for queries
because none existed; commands have one, and duplicating it for transport would
add a parallel registry and provider interface for marginal purity, against the
no-abstractions-ahead-of-need rule. The registry's EventStore.Postgres location
is acknowledged-historical (ADR 0022 records that all three registries are slated
to move to Infrastructure/Versioning in Phase 12); that migration revisits
registry homes collectively, so a transport twin now would be premature
divergence.

The Billing provider registers RefundPayment only. CapturePayment exists in the
command set but is not dispatched in v1 (the no-capture stance from Session
0009's F-0009-Q), so it is not registered. Registration is born-at-consumer: a
future commit that dispatches CapturePayment over HTTP registers it then.

## Consequences

- Three new providers register fourteen user commands: Sales (eight), Fulfillment
  (five), Billing (one). The Api host's CommandTypeRegistry holds those fourteen;
  the Workers host's holds the two timeout commands. No host registers both sets,
  because no host both serves /commands and drains the delay queue.
- The registry's uniqueness invariant spans both concerns structurally. Across the
  fourteen user-command tokens and the two timeout-command tokens, names are
  distinct, so the invariant would hold if a host ever registered both providers.
  No host registers both today: the Api host registers the three user-command
  providers, the Workers host registers OrderFulfillmentCommandTypeProvider only.
  The structural insurance is for a future-possible case, not an active concern
  today.
- Phase 7 forward consumption: Commit 12's POST /commands resolves the envelope
  type token through CommandTypeRegistry.TypeFor.
- Commit 14's GET /commands introspection reads the registry the Api host built,
  which holds only the fourteen user commands. The timeout commands live only in
  the Workers host's registry, which has no introspection endpoint, so the
  introspection endpoint exposes the user commands without filtering timeouts out.

## Trigger for revisiting

- A user command and a timeout command (or any two registered commands) collide on
  the default type-name token. The registry throws on the duplicate; the
  resolution is an explicit name through Register(Type, name).
- The Phase 12 Infrastructure/Versioning migration consolidates the registries'
  homes. The dual-concern reuse is re-examined then: whether the transport
  dispatch and the persistence round-trip want separate registries after all.
- A transport dispatch needs command metadata the persistence round-trip does not
  carry (HTTP-specific payload schema or examples beyond the name-to-type map).
  That would justify a transport-specific command catalog distinct from this
  registry.
