using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authentication;

// The established principal a host edge hands to the command bus: the actor id that lands on event
// metadata, and the actor's authoritative roles. Distinct from ForwardedIdentity, which is the wire
// claim the forwarding service asserts: a principal's Roles come from the current-roles read model
// (the authoritative source), not from the forwarded claim, so the two are different types even
// though their shape matches.
public sealed record AuthenticatedPrincipal(Guid ActorId, IReadOnlyCollection<Role> Roles);
