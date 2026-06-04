namespace EventSourcingCqrs.Domain.Abstractions;

// A read-model store that can drop one tenant's rows. The whole-table TruncateAsync
// the rebuild uses today resets every tenant at once; a per-tenant rebuild needs to
// clear one tenant alone, so ResetTenantAsync removes the given tenant's rows from
// this store's tables and leaves every other tenant's rows, and the global projection
// checkpoint, untouched.
public interface ITenantResettable
{
    Task ResetTenantAsync(TenantId tenant, CancellationToken ct = default);
}
