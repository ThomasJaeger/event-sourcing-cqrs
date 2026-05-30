namespace EventSourcingCqrs.Application.Authentication;

// Builds the AuthenticatedPrincipal for an authenticated actor by loading the actor's authoritative
// roles. The host edge resolves the actor id from the authenticated request and asks for the
// principal here; the roles come from the current-roles read model, not from the forwarded claim, so
// the principal's authority is what the system records, not what an upstream asserts.
public interface IPrincipalFactory
{
    Task<AuthenticatedPrincipal> CreateAsync(Guid actorId, CancellationToken ct);
}
