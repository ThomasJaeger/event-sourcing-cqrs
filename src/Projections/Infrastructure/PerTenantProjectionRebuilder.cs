using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Projections.Infrastructure;

// Pattern from Chapter 13 (per-tenant rebuild): rebuild one tenant's read model by
// replaying only that tenant's events, leaving every other tenant and the global
// catch-up position untouched. It reads the projection's global checkpoint once as a
// ceiling, resets the tenant's rows through the store's ITenantResettable, then replays
// the tenant's events up to that ceiling through a projection wired over a rebuild-mode
// checkpoint store. That store neither skips events (so the reset rows re-populate) nor
// advances the shared global checkpoint (so the rebuild is checkpoint-neutral). The
// ceiling keeps the replay from reaching events the projection has not globally
// processed, which would otherwise pull the global checkpoint forward.
//
// The coordinator excludes live handlers and other rebuilds for this projection until
// replay finishes. Its checkpoint lease captures the ceiling under that same lock, so a
// live write cannot commit between capturing the ceiling and resetting the tenant.
// Reset and replay are separate transactions: queries can see partial results, and a
// failed rebuild must be rerun. The shared checkpoint remains unchanged throughout.
public sealed class PerTenantProjectionRebuilder
{
    private readonly IEventStore _eventStore;
    private readonly IProjectionRebuildCoordinator _coordinator;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public PerTenantProjectionRebuilder(
        IEventStore eventStore,
        IProjectionRebuildCoordinator coordinator,
        ICurrentTenantAccessor tenantAccessor)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(tenantAccessor);
        _eventStore = eventStore;
        _coordinator = coordinator;
        _tenantAccessor = tenantAccessor;
    }

    // The caller supplies a factory that builds the projection over a given checkpoint
    // store, so the rebuild runs the projection over the rebuild-mode store while its
    // read-model writes still land in the real tables. reset is the same store's
    // tenant-scoped delete.
    public async Task RebuildAsync(
        Func<ICheckpointStore, IProjection> projectionFactory,
        ITenantResettable reset,
        TenantId tenant,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projectionFactory);
        ArgumentNullException.ThrowIfNull(reset);
        ArgumentNullException.ThrowIfNull(tenant);

        var projection = projectionFactory(new RebuildModeCheckpointStore());

        // Read the live global position once and bound the replay at it; never advance
        // it. A replay past it would pull the shared checkpoint forward over other
        // tenants' unprocessed events, which the next catch-up would then skip.
        await using var lease = await _coordinator.AcquireAsync(projection.Name, ct);

        await lease.EnsureHeldAsync(ct);
        await reset.ResetTenantAsync(tenant, ct);
        await lease.EnsureHeldAsync(ct);

        await new ProjectionReplayer(_eventStore, projection, _tenantAccessor)
            .ReplayForTenantAsync(tenant, 0, lease, ct);
        await lease.EnsureHeldAsync(ct);
    }
}
