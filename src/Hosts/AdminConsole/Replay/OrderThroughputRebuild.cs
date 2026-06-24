using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Infrastructure.SignalR;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.OrderThroughput;

namespace EventSourcingCqrs.Hosts.AdminConsole.Replay;

// The single implementation of IOrderThroughputRebuild. It builds the throughput projection over the
// rebuild-mode checkpoint store the rebuilder supplies and drives the per-tenant rebuilder, the same
// composition AdminConsoleThroughputRebuildCompositionTests resolves from the host. The page depends on
// the seam, so the rebuilder and the Postgres factory stay out of the presentation layer.
//
// Rebuild-only: authorization is the caller's concern. The host fallback gate authorizes console access
// today (ADR 0040); the per-action RebuildProjection check is deferred until an operator role below Admin
// exists (ADR 0041, Revisit when), so there is no role read here.
public sealed class OrderThroughputRebuild : IOrderThroughputRebuild
{
    private readonly PerTenantProjectionRebuilder _rebuilder;
    private readonly IOrderThroughputStore _store;
    private readonly IReadModelConnectionFactory _readModelFactory;
    private readonly PostgresPgNotifyPublisher _publisher;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public OrderThroughputRebuild(
        PerTenantProjectionRebuilder rebuilder,
        IOrderThroughputStore store,
        IReadModelConnectionFactory readModelFactory,
        PostgresPgNotifyPublisher publisher,
        ICurrentTenantAccessor tenantAccessor)
    {
        ArgumentNullException.ThrowIfNull(rebuilder);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(readModelFactory);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(tenantAccessor);
        _rebuilder = rebuilder;
        _store = store;
        _readModelFactory = readModelFactory;
        _publisher = publisher;
        _tenantAccessor = tenantAccessor;
    }

    public Task RebuildOrderThroughputAsync(TenantId tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        Func<ICheckpointStore, IProjection> projectionFactory = cp =>
            new OrderThroughputProjection(
                new PostgresOrderThroughputStore(_readModelFactory, cp, _publisher, _tenantAccessor));
        return _rebuilder.RebuildAsync(projectionFactory, (ITenantResettable)_store, tenant, ct);
    }
}
