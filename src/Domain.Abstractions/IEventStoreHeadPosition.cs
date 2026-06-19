namespace EventSourcingCqrs.Domain.Abstractions;

// The head of the global event stream: the maximum assigned global_position
// across all events, or 0 when the store is empty. Operational reads (projection
// lag) compare this against a projection's checkpoint position. This is a read of
// the global log's end, separate from the aggregate-lifecycle IEventStore
// contract, so it is its own port.
public interface IEventStoreHeadPosition
{
    Task<long> GetHeadPositionAsync(CancellationToken ct);
}
