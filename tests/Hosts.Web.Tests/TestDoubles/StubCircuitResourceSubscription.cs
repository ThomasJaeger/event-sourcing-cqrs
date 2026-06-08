using EventSourcingCqrs.Application;
using EventSourcingCqrs.Hosts.Web.Hubs;

namespace EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;

// A controllable ICircuitResourceSubscription for the OrderDetail and OrderCreate page specs. Records the
// StartAsync call (count + resource type/id) and captures the page's query+apply+marshal so a test can
// simulate the subscription delivering one notification, either by re-querying authoritative state
// (DeliverAsync) or by carrying a supplied read-model state straight to the page's apply (DeliverStateAsync).
// ThrowFromStart drives the arm-throw spec: StartAsync records the call, then faults the way the real
// async StartAsync would. No-op until the page has subscribed, which is what keeps the push specs
// red until the page is wired to arm the subscription.
internal sealed class StubCircuitResourceSubscription : ICircuitResourceSubscription
{
    public int StartCallCount { get; private set; }
    public SubscriptionResourceType? LastResourceType { get; private set; }
    public string? LastResourceId { get; private set; }
    public bool Disposed { get; private set; }
    public int DisposeCallCount { get; private set; }

    private Func<CancellationToken, Task>? _deliver;
    private Func<object?, Task>? _deliverState;
    private Exception? _startThrow;

    // Configures the next StartAsync to fault with this exception. The call is still recorded (count + args)
    // before the fault, so the arm-throw spec can assert the page armed once, caught the fault, and left the
    // subscription in place rather than disposing it.
    public void ThrowFromStart(Exception exception) => _startThrow = exception;

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

        // Surface a configured arm failure as a faulted Task, the shape the real async StartAsync produces,
        // so the awaiting page observes it identically. Record-then-fault: no deliver is captured.
        if (_startThrow is not null)
        {
            return Task.FromException(_startThrow);
        }

        _deliver = async token =>
        {
            var state = await query(token);
            await marshal(() => apply(state));
        };
        _deliverState = state => marshal(() => apply((TState)state!));
        return Task.CompletedTask;
    }

    // Simulates the dispatcher delivering one notification: re-query authoritative state and apply it
    // marshalled, exactly as the real subscription would on a Publish. No-op until the page has subscribed.
    public Task DeliverAsync(CancellationToken ct = default) => _deliver?.Invoke(ct) ?? Task.CompletedTask;

    // Simulates one push carrying a supplied read-model state straight to the page's apply, marshalled
    // through the page's own marshal. Lets a spec drive a terminal state (e.g. a Placed read model) without
    // threading it through a seeded query. No-op until the page has subscribed.
    public Task DeliverStateAsync(object? state) => _deliverState?.Invoke(state) ?? Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
