using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Queries.Sales;

// Sales bounded context's contribution to the query type registry (ADR 0022),
// parallel to SalesEventTypeProvider on the event side. Lives in Application,
// not Domain, because the query types it owns are Application types and the
// hexagonal layering rule keeps Domain free of outward dependencies. Order:
// the two order-shaped reads, the customer summary read, then the order-throughput read.
public sealed class SalesQueryTypeProvider : IQueryTypeProvider
{
    public IEnumerable<Type> GetQueryTypes() =>
    [
        typeof(ListOrders),
        typeof(GetOrderDetail),
        typeof(GetCustomerSummary),
        typeof(GetOrderThroughput),
    ];
}
