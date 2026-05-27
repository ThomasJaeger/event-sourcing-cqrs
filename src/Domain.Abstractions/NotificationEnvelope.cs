namespace EventSourcingCqrs.Domain.Abstractions;

// The notification payload published when a projection commits a row change.
// Carries projection name, resource identifier, causing event name, and the
// affected widgets. That is enough for the SignalR hub to route to the correct
// per-resource group and for the subscribing page to decide whether to
// re-query for authoritative state. Carries no row data per Chapter 13's
// notification-only-push pattern; a typical envelope is 100-300 bytes, well
// under PostgreSQL's 8000-byte NOTIFY cap which the publisher enforces at
// its boundary. ADR 0027.
public sealed record NotificationEnvelope(
    string ProjectionName,
    string ResourceId,
    string EventName,
    IReadOnlyList<string> Widgets);
