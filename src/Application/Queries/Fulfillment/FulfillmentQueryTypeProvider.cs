using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Queries.Fulfillment;

// Fulfillment bounded context's contribution to the query type registry (ADR
// 0022), parallel to FulfillmentEventTypeProvider on the event side. Lives in
// Application because the query types it owns are Application types.
public sealed class FulfillmentQueryTypeProvider : IQueryTypeProvider
{
    public IEnumerable<Type> GetQueryTypes() =>
    [
        typeof(GetAllInventoryDashboard),
        typeof(GetInventoryDashboardBySku),
    ];
}
