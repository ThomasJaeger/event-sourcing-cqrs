using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.Web.Hubs;

// The in-process replacement for the SignalR hub broadcast (ADR 0032, superseding ADR 0027). The single
// backplane reader publishes each projection notification here, and the dispatcher fans it out to the
// circuit-scoped subscribers registered for that resource, in-process, with no server-to-server SignalR
// hop. Publish never blocks on a subscriber: each subscriber drains a bounded, coalescing queue on its own
// loop, so a slow or faulting subscriber is isolated from the reader and from the other subscribers.
internal interface IResourceNotificationDispatcher
{
    // Registers a subscriber for one resource's notifications. Dispose the returned registration to stop
    // delivery and release the subscriber's queue and drain loop. Multiple subscribers may register for the
    // same key; each receives every notification routed to it.
    IDisposable Subscribe(ResourceKey key, Func<NotificationEnvelope, Task> onNotified);

    // Routes one notification to the subscribers registered for its resource. Returns immediately; delivery
    // happens off the caller's thread. An unmapped projection is logged and dropped, never misrouted.
    void Publish(NotificationEnvelope envelope);
}
