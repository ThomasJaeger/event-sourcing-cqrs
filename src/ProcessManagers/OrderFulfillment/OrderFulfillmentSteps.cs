namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// Idempotency-key and delay-queue step names for the OrderFulfillment workflow,
// shared by the PM handler (forward dispatches and timeout scheduling) and
// OrderFulfillmentCompensation (compensation dispatches and timeout cancellation).
// The timeout steps double as the IDelayQueue scheduledByStep that CancelAsync
// matches on and the idempotency-key step for the timeout command's dispatch.
internal static class OrderFulfillmentSteps
{
    public const string AuthorizePayment = "authorize-payment";
    public const string Reserve = "reserve";
    public const string ScheduleShipment = "schedule-shipment";
    public const string VoidPayment = "void-payment";
    public const string CancelOrder = "cancel-order";
    public const string Release = "release";
    public const string AwaitPaymentTimeout = "await-payment-timeout";
    public const string AwaitDispatchTimeout = "await-dispatch-timeout";
}
