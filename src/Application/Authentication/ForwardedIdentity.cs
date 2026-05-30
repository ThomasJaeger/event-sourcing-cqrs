using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authentication;

// The identity a trusted upstream (the Web host) asserts on a forwarded request: who the caller is
// and the roles the upstream believes they hold. This is a claim, not proof. In P9.3a the assertion
// is unsigned and only dev-validated (parsed); the signature check that makes it trustworthy across
// the wire lands in P9.3b at the IForwardedIdentityReader seam. The command context's authoritative
// roles are loaded from the read model, not taken from here, so a forged Roles claim cannot widen a
// principal's authority.
public sealed record ForwardedIdentity(Guid ActorId, IReadOnlyCollection<Role> Roles);
