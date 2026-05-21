namespace EventSourcingCqrs.Domain.Abstractions;

// The dispatch surface for a command a process manager originates in response
// to an event (ADR 0014). ICommandBus.SendAsync mints fresh correlation,
// causation, and actor values; this surface threads them from the causing
// event instead, so the workflow stays one correlated trace and the causation
// chain points back through the event that triggered the command. Process
// managers depend on this, not on ICommandBus, so causation propagation is a
// type-system property rather than something a handler must remember to do.
//
// The causing event arrives as EventMetadata rather than the full
// EventContext<TEvent>: the bus reads CorrelationId and EventId off it and does
// not need the payload, and EventMetadata is invariant in event type so the
// signature stays usable from a handler closed over any inbound event.
public interface ICausedCommandBus
{
    Task SendAsync(
        ICommand command,
        EventMetadata causingEventMetadata,
        SystemActor actor,
        string? idempotencyKey,
        CancellationToken ct);
}
