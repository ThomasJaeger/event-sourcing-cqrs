namespace EventSourcingCqrs.Domain.Abstractions;

// Publishes a NotificationEnvelope atomically with the caller's commit, so a
// projection that commits row changes without notifying, or notifies without
// committing, is impossible by construction. The Postgres implementation
// arranges the atomicity inside the unit-of-work's transaction; the port
// surface stays free of infrastructure types so a future adapter implements
// the same contract against its native carrier. ADR 0027.
public interface INotificationPublisher
{
    Task PublishAsync(NotificationEnvelope envelope, CancellationToken ct);
}
