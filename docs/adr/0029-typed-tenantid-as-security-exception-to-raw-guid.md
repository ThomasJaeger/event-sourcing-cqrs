# 0029. Typed TenantId as a Security Exception to the Raw-Guid Convention

## Status

Accepted (May 2026). Amends the scope of ADR 0005.

## Context

ADR 0005 routes cross-context identifiers through raw Guid. OrderId, CustomerId, and by its
own words any future cross-context identifier another bounded context refers to by reference
travel as Guid, with domain validators and command-handler tests as the type-confusion safety
net rather than the type system. That decision is sound for ordinary identifiers, and it
stays in force for them.

Phase 10 introduces tenancy by a shared-schema discriminator: one database, one schema, a
tenant component on every read-model row, in every stream id, in event metadata, and on the
principal. The tenant is not an aggregate identifier that one context references by reference.
It is an isolation boundary that appears at more sites than any other identifier in the
codebase, and a confusion at any one of those sites is a cross-tenant read or write. That is a
security failure, an incident rather than an ordinary defect. ADR 0005 names exactly this
condition, an identifier confusion whose blast radius justifies the type system over
discipline, as grounds for carving an exception.

## Decision

TenantId is a typed identifier over Guid, living in src/Domain.Abstractions. Raw Guid stays
everywhere else, including ActorId. ActorId confusion is a defect but not a cross-tenant leak,
so it remains consistent with the ADR 0005 convention and could be typed later in an ordinary
type-safety pass without being security-forced now.

The type follows the typed-id idiom already established in Domain.Abstractions by StreamId: a
single validating construction site reached through a private constructor, the static
factories From and Parse routing through it, value equality from the record, and a string
round-trip through ToString and Parse. The private constructor rejects an empty Guid with an
ArgumentException naming the value parameter, the boundary that owns the validation. Parse
rejects malformed input with its own ArgumentException naming the value parameter rather than
letting a framework parse exception leak through.

TenantId is a sealed record, not a struct. A validated struct still permits default(T), an
empty-Guid value that never passes through the validating constructor. For an ordinary value
object that is a nuisance; for the tenant boundary it is a fail-silent hazard, since an
empty-but-structurally-present tenant could reach a discriminator predicate and read across
tenants. A reference type has no such default. Its only invalid state is null, which fails
loud at use and is caught at compile time under nullable reference types. The tenant boundary
is enforced structurally rather than by downstream discipline, and a type whose default value
is a silently invalid security boundary would violate that principle at the type level.

## Placement

TenantId lives in Domain.Abstractions rather than the shared kernel because the write-side
PostgreSQL event-store adapter must reference it to stamp and validate the tenant segment of a
stream id at append. That adapter references only Domain.Abstractions. The shared kernel lives
inside Domain, which the adapter cannot see. Placing TenantId in the shared kernel would put a
type the adapter must reference where it cannot reach it without inverting the hexagonal
dependency rule. This is the same argument that places StreamId and Role in Domain.Abstractions.
Money and Address stay in the shared kernel because nothing on the write-side persistence path
references them. TenantId behaves like StreamId, not like Money.

## Consequences

- Cross-context identifiers other than the tenant stay raw Guid. ADR 0005 governs them
  unchanged. This ADR carves one exception, not a general reversal toward typed wrappers.
- A tenant value cannot be constructed empty or default, so a structurally invalid tenant
  cannot reach a discriminator predicate. The type system, not handler discipline, holds this
  boundary.
- Phase 10 commits consume the type: the stream-id format gains a tenant segment, event
  metadata carries the tenant, and the current-tenant accessor (ADR 0031) is typed by it.
  This ADR records only the type.
- The pedagogical note in ADR 0005, that a reader finishing the typed-wrapper chapter finds no
  worked typed-id example in the reference implementation, is now partly answered: TenantId is
  one, justified on a security boundary rather than as general DDD advocacy.

## Relationship to ADR 0005

ADR 0005 keeps governing ordinary cross-context identifiers as raw Guid. Its revisit trigger,
an identifier confusion whose root cause is type confusion surviving validators and tests,
fires here for the tenant on security grounds. ADR 0005's scope is amended to record that
TenantId is the carved-out, security-justified typed exception.
