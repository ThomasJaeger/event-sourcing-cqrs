namespace EventSourcingCqrs.Application.Authorization;

// The P9.5 ownership convention: within a single tenant, an actor's id is the customer id it owns, so
// the resolver returns the actor id unchanged. This is the within-tenant ownership foundation this
// commit closes; Phase 10 replaces it when an actor-to-customer mapping and tenant qualification land,
// which is why the convention sits behind IResourceOwnershipResolver rather than inline in the handlers.
public sealed class ActorIsCustomerOwnershipResolver : IResourceOwnershipResolver
{
    public Guid ResolveCustomerId(Guid actorId) => actorId;
}
