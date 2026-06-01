using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.ReadModels;

namespace EventSourcingCqrs.Application.Authentication;

// Builds the principal from the actor id by loading the actor's current roles from the read model.
// Lives in Application, not Infrastructure: it does no I/O of its own, it composes the
// ICurrentUserRolesStore port and maps the result onto an AuthenticatedPrincipal. The I/O lives in
// the Postgres store behind the port, so the factory honors the spirit of D8 (the principal-factory
// concern is Application's, the storage is Infrastructure's) while staying testable without a
// database. Loading roles here rather than trusting the forwarded claim is the security choice: the
// authoritative roles are what the system recorded, regardless of what an upstream asserted.
public sealed class CurrentRolesPrincipalFactory : IPrincipalFactory
{
    private readonly ICurrentUserRolesStore _store;

    public CurrentRolesPrincipalFactory(ICurrentUserRolesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<AuthenticatedPrincipal> CreateAsync(Guid actorId, CancellationToken ct)
    {
        var roles = await _store.GetRolesForUserAsync(actorId, ct);
        // The tenant is the default until per-user tenant onboarding lands a real lookup. The carrier
        // and the set point do not change when it does; only this value does.
        return new AuthenticatedPrincipal(actorId, roles, WellKnownTenants.Default);
    }
}
