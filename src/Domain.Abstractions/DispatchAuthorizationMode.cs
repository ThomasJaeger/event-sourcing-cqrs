namespace EventSourcingCqrs.Domain.Abstractions;

// How a command dispatch is authorized (Phase 9). None is the unenforced default for internal
// trusted-origin dispatch (the worker hosted services and the bare overloads); the two enforced
// modes gate externally reachable user dispatch and process-manager caused dispatch respectively.
// The authorization behavior enforces identically for both enforced modes and passes through only
// on None.
public enum DispatchAuthorizationMode
{
    None = 0,
    AuthenticatedUser,
    SystemActor,
}
