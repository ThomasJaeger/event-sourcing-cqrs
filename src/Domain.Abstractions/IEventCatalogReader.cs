namespace EventSourcingCqrs.Domain.Abstractions;

// Chapter 17: bounded discovery for the operator tools, separate from aggregate loading.
// Pages are ordered by ID, across tenants. Counts describe only the IDs on the returned page.
// Stream choices exclude process managers; correlation choices exclude the empty sentinel.
public interface IEventCatalogReader
{
    Task<StreamCatalogPage> ListStreamsAsync(string? afterStreamId, int pageSize, CancellationToken ct);
    Task<CorrelationCatalogPage> ListCorrelationsAsync(Guid? afterCorrelationId, int pageSize, CancellationToken ct);
}

public sealed record StreamCatalogEntry(
    string StreamId, long EventCount, DateTime LastOccurredUtc, int TenantCount);

public sealed record CorrelationCatalogEntry(
    Guid CorrelationId, long EventCount, int StreamCount, int TenantCount, DateTime LastOccurredUtc);

public sealed record StreamCatalogPage(IReadOnlyList<StreamCatalogEntry> Items, bool HasMore);
public sealed record CorrelationCatalogPage(IReadOnlyList<CorrelationCatalogEntry> Items, bool HasMore);
