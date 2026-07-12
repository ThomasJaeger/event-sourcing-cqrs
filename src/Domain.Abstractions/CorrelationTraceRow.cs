namespace EventSourcingCqrs.Domain.Abstractions;

// One stored event as the Correlation-ID Tracer reads it: the row's scalar
// columns, the payload as the JSON text the store holds, and the typed metadata.
//
// StreamId is the raw string the row carries. The reader parses it into nothing
// and classifies it as nothing, because a trace spans stream families and the
// port routes by no family. The page renders the prefix it is given.
//
// PayloadJson is the stored text. No CLR type is resolved on this path, so an
// event type the reading host never registered still renders, and what the
// operator sees is the document the store holds rather than a re-serialization
// of a round-tripped object. The events table stores payload as JSONB, a parsed
// binary form, so the text a store hands back is its own canonical rendering of
// that document: same keys and same values, with whitespace and key order the
// engine's rather than the serializer's.
//
// This record exists because EventEnvelope and ProcessManagerEventEnvelope each
// carry a deserialized payload behind a family-specific marker interface, and no
// list can hold both. OccurredUtc mirrors what those two do: the events table
// stores it as its own column, so the Tracer reports the column rather than the
// copy the metadata JSONB carries.
public sealed record CorrelationTraceRow(
    string StreamId,
    int StreamVersion,
    long GlobalPosition,
    Guid EventId,
    string EventType,
    int EventVersion,
    string PayloadJson,
    EventMetadata Metadata,
    DateTime OccurredUtc);
