namespace EventSourcingCqrs.Domain.Sales;

public enum OrderStatus
{
    Draft,
    Placed,
    Cancelled,
    Shipped
}
