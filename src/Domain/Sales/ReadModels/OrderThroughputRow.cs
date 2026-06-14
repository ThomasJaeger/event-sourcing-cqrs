namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// One row of the order-throughput read model: the count of customer-facing order
// events whose occurrence-second is SecondUtc, for the tenant the read is scoped
// to. Shape A keys a bucket on the UTC occurrence-second alone and discards the
// event type, so the meter answers "how many order events occurred in this
// second". Tenant is the store-scope dimension, not a row field, mirroring
// InventoryDashboardRow (the adapter scopes a read to the current tenant).
public sealed record OrderThroughputRow(
    DateTime SecondUtc,
    long Count);
