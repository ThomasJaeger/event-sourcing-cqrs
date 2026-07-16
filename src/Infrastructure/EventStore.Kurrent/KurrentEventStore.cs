using System.Runtime.CompilerServices;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using KurrentDB.Client;

namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

// Hand-rolled KurrentDB implementation of IEventStore (Phase 13). Self-contained per ADR 0004:
// it shares no code with the relational adapters, because every mechanic it depends on is
// engine-specific.
//
// Identity naming: StreamId.Value is the KurrentDB stream name in both directions, so no
// translation layer sits between the port and the wire (ADR 0011). Optimistic concurrency is
// KurrentDB's native expected-revision check, not a relational uniqueness constraint, so
// expectedVersion maps to a StreamState and a WrongExpectedVersionException translates to the
// contract's ConcurrencyException. There is no outbox: KurrentDB feeds projections through native
// catch-up subscriptions, so an append writes one event to one stream and nothing else (CLAUDE.md).
public sealed class KurrentEventStore : IEventStore
{
    // Excludes KurrentDB system streams (the '$' sigil) and PM streams (ADR 0013) from the $all
    // feed server-side, the engine's counterpart to the relational "stream_id NOT LIKE 'pm-%'"
    // literal. The negative-lookahead form is what the engine's server-side filter accepts. Internal
    // so the subscription dispatch service filters its $all subscription with the same expression.
    internal const string AggregateFeedStreamFilter = @"^(?!\$)(?!pm-)";

    private readonly KurrentDBClient _client;
    private readonly EventTypeRegistry _registry;
    private readonly ProcessManagerEventTypeRegistry _pmRegistry;
    private readonly JsonSerializerOptions _jsonOptions;

    public KurrentEventStore(
        KurrentDBClient client,
        EventTypeRegistry registry,
        ProcessManagerEventTypeRegistry pmRegistry,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pmRegistry);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        _client = client;
        _registry = registry;
        _pmRegistry = pmRegistry;
        _jsonOptions = jsonOptions;
    }

    public async Task AppendAsync(
        StreamId streamId,
        int expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        var data = events.Select(e => ToEventData(_registry.NameFor(e.Payload.GetType()), e));
        try
        {
            await _client.AppendToStreamAsync(
                streamId.Value, ExpectedState(expectedVersion), data, cancellationToken: ct);
        }
        catch (WrongExpectedVersionException)
        {
            throw new ConcurrencyException(streamId, expectedVersion);
        }
    }

    public async Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        StreamId streamId,
        int fromVersion = 0,
        CancellationToken ct = default)
    {
        var read = _client.ReadStreamAsync(
            Direction.Forwards, streamId.Value, StreamRevisionFrom(fromVersion), cancellationToken: ct);

        // Reading a stream that never existed is a normal, empty answer, not an error, so it is
        // driven off the read state rather than a caught exception.
        if (await read.ReadState.ConfigureAwait(false) == ReadState.StreamNotFound)
        {
            return [];
        }

        var envelopes = new List<EventEnvelope>();
        await foreach (var resolved in read.WithCancellation(ct).ConfigureAwait(false))
        {
            envelopes.Add(HydrateEvent(resolved, streamId));
        }
        return envelopes;
    }

    // Writes PM events with the same identity naming and expected-revision mapping as AppendAsync,
    // resolving type names through the PM registry (ADR 0013). No outbox row, same as the aggregate
    // path; on KurrentDB there is no outbox on either path.
    public async Task AppendProcessManagerEventsAsync(
        StreamId streamId,
        int expectedVersion,
        IReadOnlyList<ProcessManagerEventEnvelope> events,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        var data = events.Select(e => ToEventData(_pmRegistry.NameFor(e.Payload.GetType()), e));
        try
        {
            await _client.AppendToStreamAsync(
                streamId.Value, ExpectedState(expectedVersion), data, cancellationToken: ct);
        }
        catch (WrongExpectedVersionException)
        {
            throw new ConcurrencyException(streamId, expectedVersion);
        }
    }

    public async Task<IReadOnlyList<ProcessManagerEventEnvelope>> ReadProcessManagerStreamAsync(
        StreamId streamId,
        int fromVersion = 0,
        CancellationToken ct = default)
    {
        // The guard is on the shape of the stream id, checked before the client is touched: a
        // caller reaching for a PM stream with an aggregate id has a bug (ADR 0011/0013).
        if (!streamId.Value.StartsWith(StreamPrefixes.ProcessManagerPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ReadProcessManagerStreamAsync requires a process-manager stream id " +
                $"(pm- prefix); got '{streamId}'.",
                nameof(streamId));
        }

        var read = _client.ReadStreamAsync(
            Direction.Forwards, streamId.Value, StreamRevisionFrom(fromVersion), cancellationToken: ct);

        if (await read.ReadState.ConfigureAwait(false) == ReadState.StreamNotFound)
        {
            return [];
        }

        var envelopes = new List<ProcessManagerEventEnvelope>();
        await foreach (var resolved in read.WithCancellation(ct).ConfigureAwait(false))
        {
            envelopes.Add(HydrateProcessManagerEvent(resolved, streamId));
        }
        return envelopes;
    }

    // Streams the $all feed excluding system and PM streams, in commit-position order. Positions
    // are assigned at commit by KurrentDB's single-writer log, so commit order is the total order
    // with no added lock: the relational adapters take an append lock to give an IDENTITY the same
    // property, but the log has it intrinsically, and gaps come only from the log's own structure
    // rather than from rolled-back position draws (ADR 0044). The KurrentDB posture on the ADR 0044
    // invariant is recorded in ADR 0047.
    //
    // fromPosition is the resume checkpoint and it is exclusive. A forwards read from a Position is
    // inclusive of the event sitting on it, so exclusivity is a client-side skip on the commit
    // position rather than a property of the read.
    public async IAsyncEnumerable<EventEnvelope> ReadAllAsync(
        long fromPosition,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var read = _client.ReadAllAsync(
            Direction.Forwards,
            StartPositionFrom(fromPosition),
            StreamFilter.RegularExpression(AggregateFeedStreamFilter),
            cancellationToken: ct);

        await foreach (var resolved in read.WithCancellation(ct).ConfigureAwait(false))
        {
            if (checked((long)resolved.Event.Position.CommitPosition) <= fromPosition)
            {
                continue;
            }
            yield return HydrateEvent(resolved, StreamId.Parse(resolved.OriginalStreamId));
        }
    }

    // The tenant-scoped, ceilinged twin of ReadAllAsync. The server filter is the same, because
    // tenant cannot be pushed server-side: it lives in the event metadata, which KurrentDB does not
    // index, so the tenant match is a client-side compare on the metadata the relational tenant_id
    // column is computed from. fromPosition is exclusive and toPositionInclusive is inclusive; the
    // feed is commit-ordered, so the read stops once it passes the ceiling.
    public async IAsyncEnumerable<EventEnvelope> ReadAllForTenantAsync(
        TenantId tenant,
        long fromPosition,
        long toPositionInclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var read = _client.ReadAllAsync(
            Direction.Forwards,
            StartPositionFrom(fromPosition),
            StreamFilter.RegularExpression(AggregateFeedStreamFilter),
            cancellationToken: ct);

        await foreach (var resolved in read.WithCancellation(ct).ConfigureAwait(false))
        {
            var commit = checked((long)resolved.Event.Position.CommitPosition);
            if (commit <= fromPosition)
            {
                continue;
            }
            if (commit > toPositionInclusive)
            {
                yield break;
            }

            var envelope = HydrateEvent(resolved, StreamId.Parse(resolved.OriginalStreamId));
            if (envelope.Metadata.Tenant == tenant)
            {
                yield return envelope;
            }
        }
    }

    // expectedVersion is the current event count of the stream (0 means empty), so an empty stream
    // maps to NoStream and a stream of n events to the revision of its last event, n - 1, because
    // KurrentDB revisions are zero-based while the port's stream versions are one-based.
    private static StreamState ExpectedState(int expectedVersion)
        => expectedVersion <= 0
            ? StreamState.NoStream
            : StreamState.StreamRevision((ulong)(expectedVersion - 1));

    // fromVersion is a one-based, exclusive stream version; a zero-based revision to start reading
    // from equals it exactly (skip versions <= n is start at revision n), so the two-to-one
    // conversion is the identity. Non-positive reads from the start.
    private static StreamPosition StreamRevisionFrom(int fromVersion)
        => fromVersion <= 0 ? StreamPosition.Start : StreamPosition.FromInt64(fromVersion);

    private static Position StartPositionFrom(long fromPosition)
        => fromPosition <= 0 ? Position.Start : new Position((ulong)fromPosition, (ulong)fromPosition);

    private EventData ToEventData(string eventTypeName, EventEnvelope envelope)
        => BuildEventData(
            envelope.EventId, eventTypeName, envelope.Payload,
            envelope.EventVersion, envelope.OccurredUtc, envelope.Metadata);

    private EventData ToEventData(string eventTypeName, ProcessManagerEventEnvelope envelope)
        => BuildEventData(
            envelope.EventId, eventTypeName, envelope.Payload,
            envelope.EventVersion, envelope.OccurredUtc, envelope.Metadata);

    // The KurrentDB event id is the envelope's event id, so native duplicate-id idempotency attaches
    // to the contract's ids. Payload rides the data slot; everything KurrentDB cannot reconstruct on
    // read (the schema version, the occurred time, and the domain metadata) rides the metadata slot.
    private EventData BuildEventData(
        Guid eventId, string eventTypeName, object payload,
        int eventVersion, DateTime occurredUtc, EventMetadata metadata)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), _jsonOptions);
        var stored = new StoredEventMetadata(eventVersion, occurredUtc, metadata);
        var storedBytes = JsonSerializer.SerializeToUtf8Bytes(stored, _jsonOptions);
        return new EventData(Uuid.FromGuid(eventId), eventTypeName, data, storedBytes);
    }

    private EventEnvelope HydrateEvent(ResolvedEvent resolved, StreamId streamId)
        => KurrentEventHydration.ToEventEnvelope(resolved, streamId, _registry, _jsonOptions);

    private ProcessManagerEventEnvelope HydrateProcessManagerEvent(
        ResolvedEvent resolved, StreamId streamId)
        => KurrentEventHydration.ToProcessManagerEnvelope(resolved, streamId, _pmRegistry, _jsonOptions);
}
