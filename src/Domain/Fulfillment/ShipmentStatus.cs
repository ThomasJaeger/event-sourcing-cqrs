namespace EventSourcingCqrs.Domain.Fulfillment;

public enum ShipmentStatus
{
    Scheduled,
    Dispatched,
    Delivered,
    Returned
}
