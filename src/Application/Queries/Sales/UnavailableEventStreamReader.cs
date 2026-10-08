using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Queries.Sales;

// Hosts without a bounded history adapter still compose; the capability has an explicit outcome.
public sealed class UnavailableEventStreamReader : IBoundedEventStreamReader
{
    public Task<EventStreamWindow> ReadAsync(StreamId streamId, int maxEvents, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        throw new OrderHistoryUnavailableException();
    }
}
