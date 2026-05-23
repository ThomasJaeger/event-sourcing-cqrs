using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// The OrderDetail read model's persistence port. Lives in Domain.Sales.ReadModels
// because OrderDetail keys on the Sales OrderId, even though it subscribes across
// Fulfillment and Billing too: the cross-context detail view resolves those
// contexts' ids back to OrderId through the projection-private lookups below. See
// ADR 0008 (read-side ports in the keying context) and ADR 0020 (cross-aggregate
// identity resolved through projection-private lookups).
public interface IOrderDetailStore
{
    Task<IOrderDetailUnitOfWork> BeginAsync(CancellationToken ct);

    // Read paths: the header, the line items, and the timeline. The query handler
    // (commit 18) composes the three into one view; the rebuild test reads all three
    // to compare live-dispatch state against replay-from-empty state. Timeline rows
    // return ordered by GlobalPosition, the order the events were observed.
    Task<OrderDetailRow?> GetHeaderAsync(Guid orderId, CancellationToken ct);
    Task<IReadOnlyList<OrderDetailLineRow>> GetLinesAsync(Guid orderId, CancellationToken ct);
    Task<IReadOnlyList<OrderDetailTimelineRow>> GetTimelineAsync(Guid orderId, CancellationToken ct);

    // Rebuild support: drop every row from all five tables so a replay starts empty.
    Task TruncateAsync(CancellationToken ct);
}

// One projection write: the header, line, and timeline changes, any lookup change,
// and the checkpoint advance, all on one transaction. CommitAsync makes them durable
// together; DisposeAsync without a commit rolls them back.
//
// The four status transitions are named methods, not one UpdateStatusAsync, because
// each owns a distinct *_utc column. Asymmetric mutations split into named methods;
// only symmetric ones (differing by sign alone) share a signed-delta method.
// MarkReturnedAsync mirrors OrderList's: a return is a parallel column, not a status
// value (D5).
public interface IOrderDetailUnitOfWork : IAsyncDisposable
{
    Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct);

    // OrderDrafted: create the header with status Draft and no totals or address yet.
    // ON CONFLICT DO NOTHING so a redelivered draft keeps the first row.
    Task CreateHeaderAsync(Guid orderId, Guid customerId, DateTime lastUpdatedUtc, CancellationToken ct);

    // ShippingAddressSet: write the four flattened address columns.
    Task SetShippingAddressAsync(
        Guid orderId, Address shippingAddress, DateTime lastUpdatedUtc, CancellationToken ct);

    // OrderPlaced: status Placed, placed_utc, and the order total.
    Task ApplyPlacedAsync(
        Guid orderId, Money total, DateTime placedUtc, DateTime lastUpdatedUtc, CancellationToken ct);

    // OrderShipped: status Shipped, shipped_utc.
    Task ApplyShippedAsync(Guid orderId, DateTime shippedUtc, DateTime lastUpdatedUtc, CancellationToken ct);

    // OrderCancelled: status Cancelled, cancelled_utc.
    Task ApplyCancelledAsync(Guid orderId, DateTime cancelledUtc, DateTime lastUpdatedUtc, CancellationToken ct);

    // OrderCompleted: status Completed, completed_utc.
    Task ApplyCompletedAsync(Guid orderId, DateTime completedUtc, DateTime lastUpdatedUtc, CancellationToken ct);

    // ShipmentReturned: returned_utc only. The Sales status stays as it was (D5).
    Task MarkReturnedAsync(Guid orderId, DateTime returnedUtc, DateTime lastUpdatedUtc, CancellationToken ct);

    // OrderLineAdded / OrderLineRemoved. Plain insert and delete: the Order aggregate
    // forbids adding a currently-live LineId, so the primary key is never violated,
    // and a remove frees the LineId for a clean re-add.
    Task InsertLineAsync(OrderDetailLineRow row, CancellationToken ct);
    Task DeleteLineAsync(Guid orderId, Guid lineId, CancellationToken ct);

    // Every handler appends one timeline entry. ON CONFLICT DO NOTHING so a
    // redelivery at the same global position is a no-op.
    Task AppendTimelineAsync(OrderDetailTimelineRow row, CancellationToken ct);

    // Shipment lookup (ADR 0020): recorded on ShipmentScheduled, read by the three
    // shipment-update handlers to resolve OrderId. ON CONFLICT DO NOTHING on insert.
    // The mapping persists; nothing deletes it.
    Task InsertShipmentMappingAsync(OrderDetailShipmentRow row, CancellationToken ct);
    Task<Guid?> GetOrderIdByShipmentIdAsync(Guid shipmentId, CancellationToken ct);

    // Payment lookup (ADR 0020): recorded on PaymentAuthorized, read by the
    // PaymentCaptured/Refunded/Voided handlers to resolve OrderId. ON CONFLICT DO
    // NOTHING on insert. The mapping persists; nothing deletes it.
    Task InsertPaymentMappingAsync(OrderDetailPaymentRow row, CancellationToken ct);
    Task<Guid?> GetOrderIdByPaymentIdAsync(Guid paymentId, CancellationToken ct);

    Task CommitAsync(string projectionName, long position, CancellationToken ct);
}
