namespace EventSourcingCqrs.Domain.Abstractions;

public interface IQueryBus
{
    Task<TResult> AskAsync<TResult>(IQuery<TResult> query, CancellationToken ct);

    // Authenticated user-query dispatch (Phase 9). The Api /queries endpoint resolves the actor and its
    // authoritative roles and dispatches here, so both land on the query context where the
    // authorization behavior and the ownership-filtering handlers read them. The tenant rides with the
    // principal so the bus sets the current-tenant accessor at dispatch, the read-side analogue of the
    // command bus's tenant set-point. The bare overload above keeps the no-principal path: ActorId
    // empty, no roles, the default tenant. Mirrors the command bus's principal-carrying SendAsync
    // overload, adapted for a read (no idempotency key, a query is idempotent by nature).
    Task<TResult> AskAsync<TResult>(
        IQuery<TResult> query, Guid actorId, IReadOnlyCollection<Role> roles, TenantId tenant, CancellationToken ct);
}
