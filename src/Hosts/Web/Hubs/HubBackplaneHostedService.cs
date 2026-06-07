using EventSourcingCqrs.Application.SignalR;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// Pattern from Chapter 13: the dashboard server's pub/sub consumer. Subscribes
// to the hub backplane and, for each projection notification, broadcasts a
// ResourceChanged message to the matching per-resource SignalR group. The
// projection-name-to-group-prefix mapping lives here, at the transport edge, so
// the notification envelope carries no transport detail. ADR 0027.
internal sealed class HubBackplaneHostedService : BackgroundService
{
    // Maps a publishing projection to its SignalR group prefix. A projection
    // that publishes without a mapping is a defect: dispatch logs and skips
    // rather than misrouting to a wrong or empty group.
    private static readonly IReadOnlyDictionary<string, string> GroupPrefixes =
        new Dictionary<string, string>
        {
            ["order-detail"] = "order",
            ["inventory-dashboard"] = "inventory",
        };

    // The client-side method name pages handle to trigger a re-query.
    public const string ClientMethod = "ResourceChanged";

    private readonly IHubBackplaneConnection _backplane;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly ILogger<HubBackplaneHostedService> _logger;
    private readonly IResourceNotificationDispatcher _dispatcher;

    public HubBackplaneHostedService(
        IHubBackplaneConnection backplane,
        IHubContext<DashboardHub> hubContext,
        ILogger<HubBackplaneHostedService> logger,
        IResourceNotificationDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(backplane);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _backplane = backplane;
        _hubContext = hubContext;
        _logger = logger;
        _dispatcher = dispatcher;
    }

    // The backplane is a registered singleton; its IAsyncDisposable lifecycle is
    // owned by the DI container, which disposes it on host stop. This loop only
    // consumes it, honoring the stopping token at the iteration boundary.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in _backplane.SubscribeAsync(stoppingToken))
        {
            await DispatchAsync(envelope, stoppingToken);
        }
    }

    // Broadcasts one notification to its per-resource group. Internal so the
    // host's tests drive it directly without standing up the subscription loop.
    internal async Task DispatchAsync(NotificationEnvelope envelope, CancellationToken ct)
    {
        // Dual sink (ADR 0032): feed the in-process dispatcher, which does its own per-resource routing,
        // and still broadcast to the SignalR group below until the hub is retired in Commit 3.
        _dispatcher.Publish(envelope);
        if (!GroupPrefixes.TryGetValue(envelope.ProjectionName, out var prefix))
        {
            _logger.LogWarning(
                "No SignalR group mapping for projection {ProjectionName}; skipping broadcast. " +
                "ResourceId={ResourceId}",
                envelope.ProjectionName, envelope.ResourceId);
            return;
        }

        var group = HubGroup.ForResource(envelope.Tenant, prefix, envelope.ResourceId);
        await _hubContext.Clients.Group(group).SendAsync(ClientMethod, envelope, ct);
    }
}
