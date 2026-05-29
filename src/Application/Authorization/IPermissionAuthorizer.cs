using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authorization;

// The permission check. A guard asks whether a principal holding these roles is authorized for this
// permission. The consumer set is transport-side and Application-side (the command-authz behavior,
// the query handlers, and via a host the hub), with no Infrastructure consumer, so the port lives in
// Application per the consumer-set-shape rule ADR 0026 records.
public interface IPermissionAuthorizer
{
    bool IsAuthorized(IReadOnlyCollection<Role> roles, Permission permission);
}
