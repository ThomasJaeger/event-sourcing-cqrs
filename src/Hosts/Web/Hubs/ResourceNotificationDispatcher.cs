using System.Threading.Channels;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// The in-process fan-out for projection notifications (ADR 0032, superseding the SignalR hub broadcast of
// ADR 0027). One backplane reader calls Publish; the dispatcher routes each notification to the subscribers
// registered for its resource and hands it to each on a bounded, coalescing per-subscriber queue drained on
// its own loop. Publish never blocks on a subscriber and a faulting subscriber is isolated from the reader
// and from its peers, so a slow page or a thrown handler cannot stall the notification path.
//
// The registry is a plain dictionary guarded by one lock. Publish snapshots the resource's subscriber list
// under the lock and then delivers outside it, so the snapshot is consistent (a register or deregister
// racing a publish never corrupts the iteration) and delivery holds no lock. The lock spans only the
// list copy, never the queue write and never the subscriber handler.
internal sealed class ResourceNotificationDispatcher : IResourceNotificationDispatcher, IAsyncDisposable
{
    private readonly ILogger<ResourceNotificationDispatcher> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<ResourceKey, List<Subscriber>> _subscribers = [];
    private bool _disposed;

    public ResourceNotificationDispatcher(ILogger<ResourceNotificationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public IDisposable Subscribe(ResourceKey key, Func<NotificationEnvelope, Task> onNotified)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(onNotified);

        var subscriber = new Subscriber(onNotified, _logger);
        subscriber.Start();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_subscribers.TryGetValue(key, out var list))
            {
                list = [];
                _subscribers[key] = list;
            }
            list.Add(subscriber);
        }
        return new Registration(this, key, subscriber);
    }

    public void Publish(NotificationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!ResourceRouting.TryKeyFor(envelope, out var key))
        {
            _logger.LogWarning(
                "No resource routing for projection {ProjectionName}; dropping notification. ResourceId={ResourceId}",
                envelope.ProjectionName, envelope.ResourceId);
            return;
        }

        Subscriber[] targets;
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(key, out var list))
            {
                return;
            }
            // The snapshot: deliver to the subscribers present at publish time, outside the lock, so a
            // register or deregister concurrent with this publish neither throws nor blocks it.
            targets = [.. list];
        }

        foreach (var subscriber in targets)
        {
            subscriber.Enqueue(envelope);
        }
    }

    private void Remove(ResourceKey key, Subscriber subscriber)
    {
        lock (_lock)
        {
            if (_subscribers.TryGetValue(key, out var list) && list.Remove(subscriber) && list.Count == 0)
            {
                _subscribers.Remove(key);
            }
        }
        subscriber.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        List<Subscriber> draining;
        lock (_lock)
        {
            _disposed = true;
            draining = [.. _subscribers.Values.SelectMany(list => list)];
            _subscribers.Clear();
        }

        foreach (var subscriber in draining)
        {
            subscriber.Complete();
        }
        foreach (var subscriber in draining)
        {
            await subscriber.DrainCompletion;
        }
    }

    // One subscriber's bounded, coalescing mailbox and the loop that drains it. The channel holds one
    // envelope with drop-oldest: while the handler processes one notification, further notifications for the
    // same resource collapse to the latest, the in-process equivalent of the hub-side rate limiting ADR 0027
    // specified. Last-write-wins is honest because the notification carries no data: the page re-queries
    // authoritative state, so the freshest notification is the only one worth delivering.
    private sealed class Subscriber
    {
        private readonly Func<NotificationEnvelope, Task> _onNotified;
        private readonly ILogger _logger;
        private readonly Channel<NotificationEnvelope> _channel =
            Channel.CreateBounded<NotificationEnvelope>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        private Task _drain = Task.CompletedTask;

        public Subscriber(Func<NotificationEnvelope, Task> onNotified, ILogger logger)
        {
            _onNotified = onNotified;
            _logger = logger;
        }

        public Task DrainCompletion => _drain;

        public void Start() => _drain = Task.Run(DrainAsync);

        public void Enqueue(NotificationEnvelope envelope) => _channel.Writer.TryWrite(envelope);

        public void Complete() => _channel.Writer.TryComplete();

        private async Task DrainAsync()
        {
            await foreach (var envelope in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    await _onNotified(envelope);
                }
                catch (Exception ex)
                {
                    // Fault isolation: a throwing subscriber is logged and skipped, never propagated into
                    // the loop, so it cannot terminate this subscriber's delivery or any other's.
                    _logger.LogError(
                        ex,
                        "A resource notification subscriber threw; isolating it so delivery continues. " +
                        "ProjectionName={ProjectionName} ResourceId={ResourceId}",
                        envelope.ProjectionName, envelope.ResourceId);
                }
            }
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly ResourceNotificationDispatcher _dispatcher;
        private readonly ResourceKey _key;
        private readonly Subscriber _subscriber;
        private bool _disposed;

        public Registration(ResourceNotificationDispatcher dispatcher, ResourceKey key, Subscriber subscriber)
        {
            _dispatcher = dispatcher;
            _key = key;
            _subscriber = subscriber;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _dispatcher.Remove(_key, _subscriber);
        }
    }
}
