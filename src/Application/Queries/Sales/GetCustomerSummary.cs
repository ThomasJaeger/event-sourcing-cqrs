using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;

namespace EventSourcingCqrs.Application.Queries.Sales;

// Returns the per-customer summary row, or null when the customer has no
// summary (no orders observed). A thin pass-through over
// ICustomerSummaryStore.GetAsync, the same shape as ListOrders; Phase 7's API
// binding adds user-visible validation. AddApplication's assembly scan
// registers the handler, so it needs no explicit DI line.
public sealed record GetCustomerSummary(Guid CustomerId) : IQuery<CustomerSummaryRow?>;

public sealed class GetCustomerSummaryHandler : IQueryHandler<GetCustomerSummary, CustomerSummaryRow?>
{
    private readonly ICustomerSummaryStore _store;

    public GetCustomerSummaryHandler(ICustomerSummaryStore store)
    {
        _store = store;
    }

    public Task<CustomerSummaryRow?> HandleAsync(GetCustomerSummary query, CancellationToken ct)
        => _store.GetAsync(query.CustomerId, ct);
}
