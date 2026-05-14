namespace EventSourcingCqrs.Domain.Abstractions;

public interface IEventStore
{
    Task AppendAsync(
        Guid streamId,
        int expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        CancellationToken ct);

    Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        Guid streamId,
        int fromVersion = 0,
        CancellationToken ct = default);

    // Streams every stored event in global_position order, starting after
    // fromPosition (exclusive). Pass 0 to read from the start. Projections and
    // the replayer drive off this; fromPosition is the resume checkpoint.
    IAsyncEnumerable<EventEnvelope> ReadAllAsync(
        long fromPosition,
        CancellationToken ct = default);
}
