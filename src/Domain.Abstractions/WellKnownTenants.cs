namespace EventSourcingCqrs.Domain.Abstractions;

// The default tenant. Pre-tenancy corpus rows and legacy two-segment stream ids
// resolve to this tenant, and every stream is the default tenant until a second
// tenant exists. The Guid is a hand-fixed constant, not generated, because the
// legacy corpus keys off it permanently and it must not drift across restarts or
// deployments. See ADR 0029 (typed TenantId) and the EventEnvelope-tenant-and-
// migration ADR (the bi-format-additive corpus groundwork).
public static class WellKnownTenants
{
    public static readonly TenantId Default =
        TenantId.From(Guid.Parse("00000000-0000-0000-0000-000000000001"));
}
