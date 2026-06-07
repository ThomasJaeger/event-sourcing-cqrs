using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Authentication;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// A page's live subscription onto the in-process dispatcher (ADR 0032). It authorizes the subscribe through
// the same Api gate the hub used, keying the registration on the authoritative tenant the gate returns, then
// re-queries authoritative state on the initial snapshot and on every notification, marshalled onto the
// render thread. The snapshot and each notification run through one issue-token guard so the older-issued
// read never lands after a fresher one, the race the audit flagged between the authorize/snapshot and an
// early notification.
internal sealed class CircuitResourceSubscription : ICircuitResourceSubscription
{
    private readonly IResourceNotificationDispatcher _dispatcher;
    private readonly ISubscriptionAuthorizationClient _authorizationClient;
    private readonly ICircuitForwardedIdentityProvider _identityProvider;
    private readonly CancellationTokenSource _cts = new();

    private IDisposable? _registration;
    private long _issued;

    public CircuitResourceSubscription(
        IResourceNotificationDispatcher dispatcher,
        ISubscriptionAuthorizationClient authorizationClient,
        ICircuitForwardedIdentityProvider identityProvider)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(authorizationClient);
        ArgumentNullException.ThrowIfNull(identityProvider);
        _dispatcher = dispatcher;
        _authorizationClient = authorizationClient;
        _identityProvider = identityProvider;
    }

    public async Task StartAsync<TState>(
        SubscriptionResourceType resourceType,
        string resourceId,
        Func<CancellationToken, Task<TState>> query,
        Func<TState, Task> apply,
        Func<Func<Task>, Task> marshal,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(marshal);

        var actorId = await _identityProvider.GetActorIdAsync();
        var result = await _authorizationClient.AuthorizeAsync(
            actorId, new SubscriptionAuthorizationRequest(resourceType, resourceId), ct);
        // Fail closed: a deny, or an allow with no authoritative tenant, registers nothing. The tenant is
        // sourced only from the authorize response, never the client, so a subscription cannot name another
        // tenant's resource (the hub's P9.6/P10.7 posture carried to the in-process path).
        if (!result.Allowed || result.Tenant is null)
        {
            throw new ResourceSubscriptionDeniedException();
        }

        var key = new ResourceKey(TenantId.From(result.Tenant.Value), resourceType, resourceId);

        // Register before the snapshot so a change between authorize and snapshot is not missed; the issue
        // guard discards whichever of the snapshot and an early notification is the older-issued read.
        _registration = _dispatcher.Subscribe(key, _ => RefreshAsync(query, apply, marshal));
        await RefreshAsync(query, apply, marshal);
    }

    // Re-queries authoritative state and applies it, marshalled onto the render thread. Each refresh takes a
    // monotonic token and only the latest-issued result applies, so a late snapshot cannot clobber a fresher
    // notification's result and vice versa; both reads return current state, so latest-issued is at least as
    // fresh. Notification-driven refreshes run on the dispatcher's drain loop, which isolates a throw; the
    // initial snapshot's throw surfaces to the StartAsync caller.
    private async Task RefreshAsync<TState>(
        Func<CancellationToken, Task<TState>> query,
        Func<TState, Task> apply,
        Func<Func<Task>, Task> marshal)
    {
        var token = Interlocked.Increment(ref _issued);
        TState state;
        try
        {
            state = await query(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Interlocked.Read(ref _issued) != token)
        {
            return;
        }
        await marshal(() => apply(state));
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _registration?.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
