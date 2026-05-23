using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// One row of the per-customer summary read model: the C# shape of
// read_models.customer_summary. Carries the domain's Money for the lifetime
// value; PostgresCustomerSummaryStore maps it to the lifetime_value_amount and
// lifetime_value_currency columns. The row exists only for a customer with at
// least one placement, so LastOrderUtc is never null: a place-then-cancel
// customer keeps the row with OrderCount 0 and the stale LastOrderUtc, the v1
// staleness ADR 0019 names.
public sealed record CustomerSummaryRow(
    Guid CustomerId,
    int OrderCount,
    Money LifetimeValue,
    DateTime LastOrderUtc,
    DateTime LastUpdatedUtc);
