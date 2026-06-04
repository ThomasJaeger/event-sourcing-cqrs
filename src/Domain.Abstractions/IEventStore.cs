namespace EventSourcingCqrs.Domain.Abstractions;

public interface IEventStore
{
    Task AppendAsync(
        StreamId streamId,
        int expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        CancellationToken ct);

    Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        StreamId streamId,
        int fromVersion = 0,
        CancellationToken ct = default);

    // Appends process-manager events to the shared events table, skipping the
    // outbox (ADR 0013): PM events are internal coordination state, not an
    // integration contract, so they are never published to subscribers.
    // Optimistic concurrency uses the same (stream_id, stream_version)
    // constraint as AppendAsync and throws ConcurrencyException on conflict.
    Task AppendProcessManagerEventsAsync(
        StreamId streamId,
        int expectedVersion,
        IReadOnlyList<ProcessManagerEventEnvelope> events,
        CancellationToken ct);

    // Reads a process-manager stream for rehydration. Resolves payload types
    // through the PM type registry and requires a pm- prefixed StreamId,
    // failing loudly otherwise (ADR 0011/0013).
    Task<IReadOnlyList<ProcessManagerEventEnvelope>> ReadProcessManagerStreamAsync(
        StreamId streamId,
        int fromVersion = 0,
        CancellationToken ct = default);

    // Streams every stored event on non-PM streams in global_position order,
    // starting after fromPosition (exclusive). Pass 0 to read from the start.
    // Projections and the replayer drive off this; fromPosition is the resume
    // checkpoint. PM-prefixed streams are excluded (ADR 0013): projections
    // derive workflow state from aggregate events, never from PM streams.
    IAsyncEnumerable<EventEnvelope> ReadAllAsync(
        long fromPosition,
        CancellationToken ct = default);

    // Streams only the given tenant's events on non-PM streams in global_position
    // order, in the window (fromPosition, toPositionInclusive], mirroring ReadAllAsync
    // with a tenant predicate and an inclusive ceiling. A per-tenant rebuild replays one
    // tenant's events up to a captured checkpoint and no further, so the rebuild never
    // reaches events the projection has not globally processed.
    IAsyncEnumerable<EventEnvelope> ReadAllForTenantAsync(
        TenantId tenant,
        long fromPosition,
        long toPositionInclusive,
        CancellationToken ct = default);
}
