using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// The read-side fail-closed tenant resolve, shared by the read-model stores that
// predicate a read on the current tenant. Reads ICurrentTenantAccessor and throws
// the same MissingTenantContextException the command write path throws when no
// tenant is set on the async flow, so a tenant-predicated read fails closed at one
// point rather than repeating the null check per query. Returns the Guid to bind to
// the tenant_id parameter. Not a SQL composer: the stores hold no shared query
// builder, each hand-writes its SELECT, but the null-check-and-throw is the one
// piece they share, so it lives here.
internal static class ReadModelTenant
{
    public static Guid ResolveOrThrow(ICurrentTenantAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        return (accessor.Current ?? throw new MissingTenantContextException()).Value;
    }
}
