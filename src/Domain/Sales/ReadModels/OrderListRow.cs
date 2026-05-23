using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// One row of the order-list read model: the C# shape of read_models.order_list.
// Carries the domain's Money and OrderStatus directly. PostgresOrderListStore
// maps Money to the total_amount/total_currency columns and OrderStatus to text.
// IsReturned/ReturnedUtc are the Fulfillment return state composed onto the row
// (D5): a return is not a Sales status, so it lives in a parallel column pair.
public sealed record OrderListRow(
    Guid OrderId,
    Guid CustomerId,
    OrderStatus Status,
    Money Total,
    DateTime PlacedUtc,
    DateTime LastUpdatedUtc,
    bool IsReturned,
    DateTime? ReturnedUtc);
