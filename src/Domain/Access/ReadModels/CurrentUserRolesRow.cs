using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Access.ReadModels;

// One row of the current-roles read model: a user holds a role.
public sealed record CurrentUserRolesRow(Guid UserId, Role Role);
