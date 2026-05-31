namespace EventSourcingCqrs.Domain.Abstractions;

// The per-query ambient state the query-authorization behavior and the ownership-filtering handlers
// read. Parallel to ICommandContext but narrower: a query reads rather than dispatches, so it stamps
// no event metadata and carries no correlation, causation, service name, or idempotency key. It
// carries the actor, the actor's authoritative roles (Phase 9), and the authenticated-user-query gate.
// ActorId is a Guid to match the principal the host resolves; the ownership resolver maps it to the
// owning customer.
public interface IQueryContext
{
    Guid ActorId { get; }

    // The actor's authoritative roles, the set the principal factory loaded from the current-roles read
    // model. Empty on the bare (no-principal) query path; the authenticated user-query path supplies it.
    IReadOnlyCollection<Role> Roles { get; }

    // True only on the authenticated user-query path (the principal-carrying AskAsync overload). The
    // query-authorization behavior gates on it; the bare overload and any non-authenticated path leave
    // it false and pass through unenforced, the same gate discipline the command side uses.
    bool IsAuthenticatedUserQuery { get; }
}
