using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.SignalR;

// The hub backplane delivers projection notifications to the SignalR hub at
// Hosts.Web. SubscribeAsync yields envelopes as they arrive on the underlying
// pub/sub channel; the implementation owns the channel's connection lifecycle
// and the deserialization from the carrier's payload format to
// NotificationEnvelope. The hub iterates the enumerable through a hosted
// service that broadcasts each envelope to the matching per-resource SignalR
// group. ADR 0027.
//
// The port lives at the Application layer because its only consumer is
// Hosts.Web; no Infrastructure consumer surfaces. Application placement is
// correct per ADR 0026's hexagonal-inversion reasoning, the same shape that
// kept QueryTypeRegistry in Application. The Postgres implementation in
// Infrastructure.SignalR depends on Application for this contract; a future
// adapter against a different carrier (Redis Pub/Sub, a managed bus)
// implements the same port without changing the abstraction.
public interface IHubBackplaneConnection
{
    IAsyncEnumerable<NotificationEnvelope> SubscribeAsync(CancellationToken ct);
}
