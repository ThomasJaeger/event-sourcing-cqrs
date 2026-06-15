using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;

namespace EventSourcingCqrs.Application.Queries.Sales;

// Returns every per-second order-throughput bucket, ordered by second, for the
// admin throughput dashboard. A thin pass-through over
// IOrderThroughputStore.GetBucketsAsync; AddApplication's assembly scan registers
// the handler.
// Requires ViewOrderThroughput, which only Admin holds via the Enum.GetValues auto-grant. Throughput is
// tenant-wide operational data with no owning customer, so there is no ownership filter; the permission
// gate is the whole enforcement.
public sealed record GetOrderThroughput()
    : IQuery<IReadOnlyList<OrderThroughputRow>>, IAuthorizedQuery
{
    public static Permission RequiredPermission => Permission.ViewOrderThroughput;
}

public sealed class GetOrderThroughputHandler
    : IQueryHandler<GetOrderThroughput, IReadOnlyList<OrderThroughputRow>>
{
    private readonly IOrderThroughputStore _store;

    public GetOrderThroughputHandler(IOrderThroughputStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<OrderThroughputRow>> HandleAsync(
        GetOrderThroughput query, CancellationToken ct)
        => _store.GetBucketsAsync(ct);
}
