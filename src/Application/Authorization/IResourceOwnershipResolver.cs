namespace EventSourcingCqrs.Application.Authorization;

// Maps an authenticated actor to the customer whose resources it owns, so an owner-scoped query (a
// customer reading its own orders) filters on a customer id the handler can compare against a read-model
// row. The seam exists so the actor-to-customer mapping is one type rather than a thread through every
// handler: when Phase 10 lands an explicit mapping and tenant qualification, the implementation changes
// and the handlers do not.
public interface IResourceOwnershipResolver
{
    Guid ResolveCustomerId(Guid actorId);
}
