namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// The workflow states for OrderFulfillmentProcessManager. NotStarted is the
// explicit zero so a freshly-constructed PM (Version 0, no events) does not read
// as AwaitingPayment; OrderFulfillmentStarted is the only transition that leaves
// NotStarted. Chapter 10's branch-4 CancellingShipment state is absent: Shipment
// has no Cancel operation, so branch 4 compensates with release-plus-refund, the
// same work as branch 3 from a different trigger.
public enum OrderFulfillmentState
{
    NotStarted,
    AwaitingPayment,
    AwaitingInventory,
    AwaitingShipment,
    AwaitingDispatch,
    AwaitingDelivery,
    ReleasingInventory,
    RefundingPayment,
    Completed,
    Cancelled
}
