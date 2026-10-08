namespace EventSourcingCqrs.Domain.Abstractions;

// A stored event in a bounded history could not be interpreted. Transport/database failures
// remain their original exceptions so callers do not confuse an unavailable store with bad history.
public sealed class EventStreamReadException(Exception innerException)
    : Exception("A stored event could not be interpreted.", innerException);
