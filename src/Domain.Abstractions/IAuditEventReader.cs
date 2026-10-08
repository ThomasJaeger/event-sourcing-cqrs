namespace EventSourcingCqrs.Domain.Abstractions;

// Chapter 17: read recorded facts across tenants for an authorized operator. The upper position
// bounds subsequent pages; it is not a transaction held open across browser requests.
public interface IAuditEventReader
{
    Task<AuditEventPage> ReadPageAsync(AuditEventFilter filter, long? upperPosition,
        long? beforePosition, int pageSize, CancellationToken ct);
}

public sealed record AuditEventFilter(
    string? StreamId = null, Guid? CorrelationId = null, TenantId? Tenant = null, Guid? ActorId = null);

// JSON is the store's rendering of its stored documents, without CLR payload resolution or upcasting.
public sealed record AuditEventRow(
    long GlobalPosition, string StreamId, int StreamVersion, Guid EventId,
    string EventType, int EventVersion, DateTime OccurredUtc, EventMetadata Metadata,
    string PayloadJson, string MetadataJson);

public sealed record AuditEventPage(
    IReadOnlyList<AuditEventRow> Items, long UpperPosition, long? NextBeforePosition);
