namespace EventSourcingCqrs.Domain.Abstractions;

// Carries the per-command ambient state EventMetadata.ForCommand needs to
// stamp correlation, causation, actor, and source onto every event the
// handler's aggregate raises. UtcNow is a method, not a property, so a clock
// abstraction can return the same instant for every read inside one command.
// ActorId is a Guid to match EventMetadata.ActorId; Phase 7's HTTP middleware
// maps an authenticated principal to a Guid before populating the context.
public interface ICommandContext
{
    Guid CorrelationId { get; }

    Guid CausationCommandId { get; }

    Guid ActorId { get; }

    // The actor's roles (Phase 9): on the authenticated user-dispatch path the authoritative set the
    // principal factory loaded from the current-roles read model, and on the process-manager caused
    // path Role.System. Empty on the no-principal paths: the bare user-dispatch overloads, the worker
    // writes, and the System fallback. The command-authorization behavior reads this to decide a
    // command's required permission against the actor's roles.
    IReadOnlyCollection<Role> Roles { get; }

    // How this dispatch is authorized (Phase 9). The command-authorization behavior gates on it:
    // AuthenticatedUser and SystemActor are enforced against Roles; the worker and bare paths leave it
    // None and pass through unenforced, so a None pass-through means a throw never reaches the
    // dispatch-failure wrapper that catches only domain and concurrency failures.
    DispatchAuthorizationMode AuthorizationMode { get; }

    string ServiceName { get; }

    // Set only on process-manager dispatch through CausedCommandBus (ADR 0014);
    // null on the user-dispatch path. IdempotencyBehavior (ADR 0016, a later
    // commit) reads it to dedupe retried commands. Nullable so the construction
    // sites that never set it stay unchanged.
    string? IdempotencyKey { get; }

    DateTimeOffset UtcNow();
}
