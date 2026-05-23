using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// One row of CustomerSummaryProjection's projection-private per-order lookup
// (read_models.customer_summary_orders, ADR 0019 / D8). Recorded on OrderPlaced
// and read on OrderCancelled to recover the customer and total the cancel event
// does not carry. Not a public read model: no store port exposes it directly
// and no query handler reads it.
public sealed record CustomerSummaryOrderRow(
    Guid CustomerId,
    Guid OrderId,
    Money Total,
    DateTime PlacedUtc);
