namespace EventSourcingCqrs.Hosts.Web.Components;

/// <summary>
/// State of the pending-badge state machine for an optimistic-UI dispatch.
/// Idle: no command in flight. Pending: command dispatched, waiting for
/// settlement via polling. Settled: read model confirms the expected state.
/// Failed: dispatch or polling-timeout failure surfaced; the consumer
/// surfaces a retry affordance.
/// </summary>
public enum BadgeState
{
    Idle,
    Pending,
    Settled,
    Failed,
}
