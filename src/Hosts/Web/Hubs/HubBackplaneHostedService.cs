using EventSourcingCqrs.Application.SignalR;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// Pattern from Chapter 13: the dashboard server's pub/sub consumer. Subscribes to the hub backplane and
// feeds each projection notification to the in-process dispatcher, which fans it out to the circuit-scoped
// subscribers. The SignalR hub broadcast it used to do retired with the hub (ADR 0032, superseding ADR
// 0027); routing now lives entirely in the dispatcher (ResourceRouting), so this reader holds no transport
// detail.
internal sealed class HubBackplaneHostedService : BackgroundService
{
    private readonly IHubBackplaneConnection _backplane;
    private readonly IResourceNotificationDispatcher _dispatcher;

    public HubBackplaneHostedService(
        IHubBackplaneConnection backplane,
        IResourceNotificationDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(backplane);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _backplane = backplane;
        _dispatcher = dispatcher;
    }

    // The backplane is a registered singleton; its IAsyncDisposable lifecycle is owned by the DI container,
    // which disposes it on host stop. This loop only consumes it, honoring the stopping token at the
    // iteration boundary.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in _backplane.SubscribeAsync(stoppingToken))
        {
            Dispatch(envelope);
        }
    }

    // Feeds one notification to the in-process dispatcher. Internal so the host's tests drive the feed
    // directly without standing up the subscription loop. The dispatcher owns routing and the unmapped-
    // projection drop, so this is a straight handoff.
    internal void Dispatch(NotificationEnvelope envelope) => _dispatcher.Publish(envelope);
}
