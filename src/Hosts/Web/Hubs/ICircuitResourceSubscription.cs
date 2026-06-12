using EventSourcingCqrs.Application;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// A Blazor page's handle onto the in-process dispatcher (ADR 0032, the in-process successor to the page's
// SignalR hub subscription of ADR 0027). The page calls StartAsync with its own re-query and apply; the
// subscription authorizes through the Api gate, registers under the authoritative tenant, takes an initial
// snapshot, and re-runs the page's query on every notification, marshalled onto the render thread and
// guarded so a late read cannot clobber a fresher one. The page deregisters by disposing the subscription
// from its own DisposeAsync, so the singleton dispatcher never roots a closed circuit.
internal interface ICircuitResourceSubscription : IAsyncDisposable
{
    // Authorizes and starts a live subscription for one resource. query reads authoritative state; apply
    // updates the page from it; marshal is the component's InvokeAsync, the boundary onto the render thread.
    // Throws ResourceSubscriptionDeniedException when the gate refuses or returns no tenant, and
    // TimeoutException when the authorize or snapshot leg times out (ADRs 0035, 0036); cancellation
    // propagates unchanged.
    Task StartAsync<TState>(
        SubscriptionResourceType resourceType,
        string resourceId,
        Func<CancellationToken, Task<TState>> query,
        Func<TState, Task> apply,
        Func<Func<Task>, Task> marshal,
        CancellationToken ct);
}
