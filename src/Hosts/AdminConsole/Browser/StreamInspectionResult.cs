using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.AdminConsole.Browser;

// The outcome of inspecting one stream. Found carries the events shaped for display; every other outcome
// carries an empty event list, and the page renders the message that matches the outcome.
public enum StreamInspectionOutcome
{
    Found,
    Empty,
    ProcessManagerUnsupported,
    InvalidFormat,
}

// One event shaped for operator display: the scalar header fields, the payload re-serialized to canonical
// JSON through the host's JsonSerializerOptions, and the full metadata kept for on-demand display.
public sealed record InspectedEvent(
    int StreamVersion,
    string EventType,
    int EventVersion,
    DateTime OccurredUtc,
    long GlobalPosition,
    string PayloadJson,
    EventMetadata Metadata);

public sealed record StreamInspectionResult(
    StreamInspectionOutcome Outcome,
    IReadOnlyList<InspectedEvent> Events);
