using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using KurrentDB.Client;

namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

// The adapter's read-side hydration, shared by the store's stream reads and the subscription dispatch
// service so both reconstruct an event from one KurrentDB record through one path (self-contained
// within the adapter, ADR 0004). A ResolvedEvent carries the payload in its data slot and the fields
// KurrentDB cannot reconstruct natively (schema version, occurred time, domain metadata) in its
// metadata slot, serialized with the adapter's JSON options.
internal static class KurrentEventHydration
{
    public static EventEnvelope ToEventEnvelope(
        ResolvedEvent resolved,
        StreamId streamId,
        EventTypeRegistry registry,
        JsonSerializerOptions jsonOptions)
    {
        var record = resolved.Event;
        var stored = ReadStoredMetadata(record.Metadata, jsonOptions);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: checked((int)(record.EventNumber.ToUInt64() + 1)),
            EventId: record.EventId.ToGuid(),
            EventType: record.EventType,
            EventVersion: stored.EventVersion,
            Payload: DeserializeDomainEvent(record.EventType, record.Data, streamId, registry, jsonOptions),
            Metadata: stored.Metadata,
            OccurredUtc: stored.OccurredUtc,
            GlobalPosition: checked((long)record.Position.CommitPosition));
    }

    public static ProcessManagerEventEnvelope ToProcessManagerEnvelope(
        ResolvedEvent resolved,
        StreamId streamId,
        ProcessManagerEventTypeRegistry pmRegistry,
        JsonSerializerOptions jsonOptions)
    {
        var record = resolved.Event;
        var stored = ReadStoredMetadata(record.Metadata, jsonOptions);
        return new ProcessManagerEventEnvelope(
            StreamId: streamId,
            StreamVersion: checked((int)(record.EventNumber.ToUInt64() + 1)),
            EventId: record.EventId.ToGuid(),
            EventType: record.EventType,
            EventVersion: stored.EventVersion,
            Payload: DeserializeProcessManagerEvent(
                record.EventType, record.Data, streamId, pmRegistry, jsonOptions),
            Metadata: stored.Metadata,
            OccurredUtc: stored.OccurredUtc,
            GlobalPosition: checked((long)record.Position.CommitPosition));
    }

    // The subscription dispatch service's mapping: a matched $all record becomes the OutboxMessage the
    // in-process dispatcher plays into projections and process managers. There is no outbox row, so the
    // $all commit position stands in as the message id, which is already the record's identity in the
    // log. AttemptCount is 0: the subscription is at-least-once and redelivers a whole event on
    // reconnect rather than tracking a per-message attempt count the way the outbox does.
    public static OutboxMessage ToOutboxMessage(
        ResolvedEvent resolved,
        EventTypeRegistry registry,
        JsonSerializerOptions jsonOptions)
    {
        var record = resolved.Event;
        var streamId = StreamId.Parse(resolved.OriginalStreamId);
        var stored = ReadStoredMetadata(record.Metadata, jsonOptions);
        var globalPosition = checked((long)record.Position.CommitPosition);
        return new OutboxMessage(
            OutboxId: globalPosition,
            EventId: record.EventId.ToGuid(),
            EventType: record.EventType,
            Event: DeserializeDomainEvent(record.EventType, record.Data, streamId, registry, jsonOptions),
            Metadata: stored.Metadata,
            GlobalPosition: globalPosition,
            AttemptCount: 0);
    }

    private static IDomainEvent DeserializeDomainEvent(
        string eventType,
        ReadOnlyMemory<byte> json,
        StreamId streamId,
        EventTypeRegistry registry,
        JsonSerializerOptions jsonOptions)
    {
        Type clrType;
        try
        {
            clrType = registry.TypeFor(eventType);
        }
        catch (UnknownEventTypeException ex)
        {
            throw new UnknownEventTypeException(eventType, streamId, ex);
        }
        return (IDomainEvent)JsonSerializer.Deserialize(json.Span, clrType, jsonOptions)!;
    }

    private static IProcessManagerEvent DeserializeProcessManagerEvent(
        string eventType,
        ReadOnlyMemory<byte> json,
        StreamId streamId,
        ProcessManagerEventTypeRegistry pmRegistry,
        JsonSerializerOptions jsonOptions)
    {
        Type clrType;
        try
        {
            clrType = pmRegistry.TypeFor(eventType);
        }
        catch (UnknownEventTypeException ex)
        {
            throw new UnknownEventTypeException(eventType, streamId, ex);
        }
        return (IProcessManagerEvent)JsonSerializer.Deserialize(json.Span, clrType, jsonOptions)!;
    }

    // Mirrors EventMetadataReader's tenant coalesce: STJ leaves the non-nullable Tenant null for a
    // metadata shape that carries no tenant, and the default tenant is the absence of one.
    private static StoredEventMetadata ReadStoredMetadata(
        ReadOnlyMemory<byte> json, JsonSerializerOptions jsonOptions)
    {
        var stored = JsonSerializer.Deserialize<StoredEventMetadata>(json.Span, jsonOptions)
            ?? throw new InvalidOperationException("Event metadata deserialized to null.");

        TenantId? tenant = stored.Metadata.Tenant;
        return tenant is null
            ? stored with { Metadata = stored.Metadata with { Tenant = WellKnownTenants.Default } }
            : stored;
    }
}

// The KurrentDB metadata slot: the envelope fields KurrentDB does not carry natively (the schema
// version and the occurred time) plus the domain metadata. Serialized with the adapter's JSON options
// so the nested tenant scalar and non-ASCII survive the round trip. The store writes it on append and
// the hydration reads it back, so it lives beside them both.
internal sealed record StoredEventMetadata(
    int EventVersion, DateTime OccurredUtc, EventMetadata Metadata);
