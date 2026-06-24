using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.AdminConsole.Replay;

// The operator-triggered rebuild of the order-throughput read model for one tenant: it resets that
// tenant's buckets and replays the tenant's events back into them, leaving every other tenant and the
// shared global checkpoint untouched. The page depends on this narrow seam rather than orchestrating the
// rebuilder and its Postgres factory inline, so the host composes the single implementation.
//
// Authorization is the caller's concern: the AdminConsole host fallback gate authorizes console access
// today (ADR 0040). The per-action RebuildProjection check is deferred until an operator role below
// Admin exists (ADR 0041, Revisit when), so this operation carries no role read yet.
public interface IOrderThroughputRebuild
{
    Task RebuildOrderThroughputAsync(TenantId tenant, CancellationToken ct);
}
