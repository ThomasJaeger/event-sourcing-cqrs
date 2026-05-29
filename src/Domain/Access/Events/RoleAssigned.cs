using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Access.Events;

// A role was assigned to a user. The Access surface is event-sourced so authorization changes are
// auditable through the same event store as every other change. Role lives in Domain.Abstractions
// (ADR 0028); the event carries it as part of its payload.
public sealed record RoleAssigned(Guid UserId, Role Role) : IDomainEvent;
