namespace EventSourcingCqrs.Domain.Abstractions;

// The canonical projection checkpoint names. Each projection's Name property and
// the name-only roster (IProjectionRoster) both reference these constants, so a
// name lives in exactly one place and cannot drift between a projection and the
// roster. The values are the checkpoint keys ICheckpointStore is keyed on.
public static class ProjectionNames
{
    public const string OrderList = "order-list";
    public const string SkuToInventoryId = "sku-to-inventory-id";
    public const string OrderIdToPaymentId = "order-id-to-payment-id";
    public const string CustomerSummary = "customer-summary";
    public const string InventoryDashboard = "inventory-dashboard";
    public const string OrderThroughput = "order-throughput";
    public const string OrderDetail = "order-detail";
    public const string CurrentRoles = "current-roles";
}
