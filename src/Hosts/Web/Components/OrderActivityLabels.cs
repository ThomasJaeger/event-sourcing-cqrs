using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales.Events;

namespace EventSourcingCqrs.Hosts.Web.Components;

public static class OrderActivityLabels
{
    public static string For(string eventType) => eventType switch
    {
        nameof(OrderDrafted) => "Order draft created",
        nameof(OrderLineAdded) => "Item added",
        nameof(OrderLineRemoved) => "Item removed",
        nameof(ShippingAddressSet) => "Shipping address saved",
        nameof(OrderPlaced) => "Order placed",
        nameof(OrderShipped) => "Order shipped",
        nameof(OrderCancelled) => "Order cancelled",
        nameof(OrderCompleted) => "Order completed",
        nameof(ShipmentScheduled) => "Shipment scheduled",
        nameof(ShipmentDispatched) => "Shipment dispatched",
        nameof(ShipmentDelivered) => "Shipment delivered",
        nameof(ShipmentReturned) => "Shipment returned",
        nameof(PaymentAuthorized) => "Payment authorized",
        nameof(PaymentCaptured) => "Payment captured",
        nameof(PaymentRefunded) => "Refund issued",
        nameof(PaymentVoided) => "Payment authorization voided",
        _ => "Other order activity",
    };
}
