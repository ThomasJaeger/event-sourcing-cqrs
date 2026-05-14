using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Projections.OrderList;

// One row of the order-list read model: the C# shape of read_models.order_list.
// Carries the domain's Money and OrderStatus directly. PostgresOrderListStore
// maps Money to the total_amount/total_currency columns and OrderStatus to text.
public sealed record OrderListRow(
    Guid OrderId,
    Guid CustomerId,
    OrderStatus Status,
    Money Total,
    DateTime PlacedUtc,
    DateTime LastUpdatedUtc);
