namespace EventSourcingCqrs.Domain.Abstractions;

// A bounded aggregate-history read, separate from the repository's complete rehydration path.
// Events begin at stream version 1. HasMore means the returned prefix is not the complete stream.
public interface IBoundedEventStreamReader
{
    Task<EventStreamWindow> ReadAsync(StreamId streamId, int maxEvents, CancellationToken ct);
}

public sealed record EventStreamWindow(IReadOnlyList<EventEnvelope> Events, bool HasMore);
