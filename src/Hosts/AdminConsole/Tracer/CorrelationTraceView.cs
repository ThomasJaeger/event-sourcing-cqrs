using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tracer;

// The outcome of tracing one correlation id. Found carries the rows shaped for display; every other outcome
// carries an empty row list, and the page renders the message that matches the outcome.
//
// InvalidFormat and SentinelRefused are separate because they are separate operator situations. A string
// that is not a Guid is a typo, and the page answers it by naming the expected form. Guid.Empty parses and
// is refused for a different reason: it is a sentinel rather than a workflow, and post-ADR 0042 it matches
// only the process-manager rows written before that fix. One outcome carrying both would force the seam to
// hand the page its wording; two lets the page own both messages, the boundary the Browser's seam keeps.
public enum CorrelationTraceOutcome
{
    Found,
    Empty,
    SentinelRefused,
    InvalidFormat,
}

// The event family a row belongs to, derived from its stream-id prefix. The page renders it so an operator
// can tell a process manager's own state transitions from the aggregate events around them in one list.
public enum TracedEventKind
{
    Aggregate,
    ProcessManager,
}

// One traced event shaped for operator display. It mirrors InspectedEvent, the Browser's display record, and
// adds the two things a cross-stream read needs and a single-stream read does not: the stream the row came
// from and the family it belongs to. Metadata rides whole, as it does on InspectedEvent, so the page reads
// correlation, causation, actor, source, and the row's tenant as typed values off one member.
//
// PayloadJson is the text the store holds rather than a re-serialization. The trace resolves no payload type
// (ADR 0043), which is what lets one read carry both families.
public sealed record TracedEvent(
    string StreamId,
    TracedEventKind Kind,
    int StreamVersion,
    string EventType,
    int EventVersion,
    DateTime OccurredUtc,
    long GlobalPosition,
    string PayloadJson,
    EventMetadata Metadata);

// The view the Tracer page renders: the rows, and whether the store held more of them than the seam's cap
// allowed back.
//
// Named View rather than Result because Domain.Abstractions already owns CorrelationTraceResult, the port's
// raw return (ADR 0043), and the seam's implementation reads both at one call site.
public sealed record CorrelationTraceView(
    CorrelationTraceOutcome Outcome,
    IReadOnlyList<TracedEvent> Events,
    bool Truncated);
