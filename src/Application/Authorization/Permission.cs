namespace EventSourcingCqrs.Application.Authorization;

// A named capability an operation requires. Authorization is permission-based: a guard asks
// whether the principal holds the permission an operation requires, never whether it holds a
// named role. The set is curated at resource-and-operation granularity; the command-authz
// commit reconciles each command to a required permission and extends this enumeration as it does.
public enum Permission
{
    PlaceOrder,
    ViewOrder,
    CancelOrder,
    ManageOrderLines,
    ViewCustomer,
    ViewInventory,
    ViewShipment,
    ProcessReturn,
    ReserveInventory,
    ReleaseInventory,
    AuthorizePayment,
    CapturePayment,
    RefundPayment,
    ScheduleShipment,
    DispatchShipment,
    MarkOrderCompleted,
}
