using EventSourcingCqrs.Application;
using EventSourcingCqrs.Hosts.Web.Hubs;

namespace EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;

// A controllable ICircuitResourceSubscription for the OrderDetail page specs. Records the StartAsync call
// (count + resource type/id) and captures the page's query+apply+marshal so a test can simulate the
// subscription delivering one notification (re-query + marshalled apply) via DeliverAsync. No-op until the
// page has actually subscribed, which is what keeps the Commit 2 push specs red.
internal sealed class StubCircuitResourceSubscription : ICircuitResourceSubscription
{
    public int StartCallCount { get; private set; }
    public SubscriptionResourceType? LastResourceType { get; private set; }
    public string? LastResourceId { get; private set; }
    public bool Disposed { get; private set; }

    private Func<CancellationToken, Task>? _deliver;

    public Task StartAsync<TState>(
        SubscriptionResourceType resourceType,
        string resourceId,
        Func<CancellationToken, Task<TState>> query,
        Func<TState, Task> apply,
        Func<Func<Task>, Task> marshal,
        CancellationToken ct)
    {
        StartCallCount++;
        LastResourceType = resourceType;
        LastResourceId = resourceId;
        _deliver = async token =>
        {
            var state = await query(token);
            await marshal(() => apply(state));
        };
        return Task.CompletedTask;
    }

    // Simulates the dispatcher delivering one notification: re-query authoritative state and apply it
    // marshalled, exactly as the real subscription would on a Publish. No-op until the page has subscribed.
    public Task DeliverAsync(CancellationToken ct = default) => _deliver?.Invoke(ct) ?? Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
