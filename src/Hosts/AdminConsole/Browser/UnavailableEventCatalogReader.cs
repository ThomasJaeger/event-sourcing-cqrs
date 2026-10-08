using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.AdminConsole.Browser;

// The capability hides catalog controls. This guard prevents accidental reads if a caller
// ignores it, without inventing a costly fallback on another provider.
public sealed class UnavailableEventCatalogReader(EventCatalogAvailability availability) : IEventCatalogReader
{
    public Task<StreamCatalogPage> ListStreamsAsync(string? afterStreamId, int pageSize, CancellationToken ct)
        => throw new NotSupportedException(availability.UnavailableReason);

    public Task<CorrelationCatalogPage> ListCorrelationsAsync(Guid? afterCorrelationId, int pageSize, CancellationToken ct)
        => throw new NotSupportedException(availability.UnavailableReason);
}
