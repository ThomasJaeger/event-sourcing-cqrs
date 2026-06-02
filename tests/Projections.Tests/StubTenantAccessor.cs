using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Projections.Tests;

// A settable ICurrentTenantAccessor for the read-model tenant-guard tests and the
// stores' tenant-accessor constructor argument. Born here because Projections.Tests
// sits below Application by design and does not reference it, so the real
// AsyncLocalCurrentTenantAccessor is out of reach; this stub carries only the
// settable Current the tests set. Async-local flow isolation is proven against the
// real accessor in the Application.Tests bus tests, not here.
internal sealed class StubTenantAccessor : ICurrentTenantAccessor
{
    public TenantId? Current { get; set; }
}
