using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Access.Events;

// A role was revoked from a user.
public sealed record RoleRevoked(Guid UserId, Role Role) : IDomainEvent;
