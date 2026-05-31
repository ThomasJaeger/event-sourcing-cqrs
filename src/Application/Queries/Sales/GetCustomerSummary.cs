using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;

namespace EventSourcingCqrs.Application.Queries.Sales;

// Returns the per-customer summary row, or null when the customer has no
// summary (no orders observed). A thin pass-through over
// ICustomerSummaryStore.GetAsync, the same shape as ListOrders; Phase 7's API
// binding adds user-visible validation. AddApplication's assembly scan
// registers the handler, so it needs no explicit DI line.
// Requires ViewCustomer, which Support and Admin hold but Customer does not, so this stays an
// operational view: a customer does not read its own summary through this query under the landed
// permission sets (ADR 0028). No ownership filter; the permission gate is the whole enforcement.
public sealed record GetCustomerSummary(Guid CustomerId)
    : IQuery<CustomerSummaryRow?>, IAuthorizedQuery
{
    public static Permission RequiredPermission => Permission.ViewCustomer;
}

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
