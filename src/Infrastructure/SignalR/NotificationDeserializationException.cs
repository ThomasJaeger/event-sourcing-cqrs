namespace EventSourcingCqrs.Infrastructure.SignalR;

// Thrown when the backplane receives a NOTIFY payload that fails to
// deserialize as a NotificationEnvelope. The most likely cause is a
// publisher-side change that emits an incompatible envelope shape: a missing
// required field, a renamed property, a type mismatch. Surfacing this as a
// named exception at the boundary tells an operator that the payload itself
// was the problem, distinct from a transport failure or a connection drop.
// The original JsonException is wrapped so the operator can correlate against
// the specific deserialization error.
//
// Not thrown from the listener loop itself: a per-payload deserialization
// failure is logged with this exception type and the listener continues, so a
// single malformed payload does not cycle the connection. The type's role is
// to be the structured-logging entry an operator searches for in a
// notification-delivery investigation.
public sealed class NotificationDeserializationException : Exception
{
    public NotificationDeserializationException(string payload, Exception innerException)
        : base(
            $"Failed to deserialize backplane notification payload as NotificationEnvelope. " +
            $"Payload: '{payload}'.",
            innerException)
    {
        Payload = payload;
    }

    public string Payload { get; }
}
