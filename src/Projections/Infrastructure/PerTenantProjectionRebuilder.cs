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
// The rebuild is non-transactional reset-then-replay against a consumer quiesced for the
// tenant: between the reset and the end of the replay the tenant's read model is
// partially populated, which is why the operational contract runs it while the tenant's
// catch-up is paused.
public sealed class PerTenantProjectionRebuilder
{
    private readonly IEventStore _eventStore;
    private readonly ICheckpointStore _checkpointStore;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public PerTenantProjectionRebuilder(
        IEventStore eventStore,
        ICheckpointStore checkpointStore,
        ICurrentTenantAccessor tenantAccessor)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(tenantAccessor);
        _eventStore = eventStore;
        _checkpointStore = checkpointStore;
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
        var ceiling = await _checkpointStore.GetPositionAsync(projection.Name, ct);

        await reset.ResetTenantAsync(tenant, ct);

        await new ProjectionReplayer(_eventStore, projection, _tenantAccessor)
            .ReplayForTenantAsync(tenant, 0, ceiling, ct);
    }
}
