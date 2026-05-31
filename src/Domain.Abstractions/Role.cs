namespace EventSourcingCqrs.Domain.Abstractions;

// A named role a principal can hold. A principal can hold several. The System role holds the
// permission set process managers exercise; the caused-command bus stamps it onto the command
// context (SystemActor.SystemRoles) so caused dispatch is authorized under it.
//
// Role lives in Domain.Abstractions rather than alongside the authorization policy in Application
// (ADR 0028) because the event-sourced Access surface serializes it: RoleAssigned and RoleRevoked
// carry a Role, the current-roles read model keys on it, and the Infrastructure adapter that reads
// the read model must see the type without referencing Application (which would invert the
// hexagonal rule ADR 0026 records). Permission stays in Application: it is runtime authorization
// policy and never crosses the persistence boundary.
public enum Role
{
    Customer,
    Support,
    Admin,
    System,
}
