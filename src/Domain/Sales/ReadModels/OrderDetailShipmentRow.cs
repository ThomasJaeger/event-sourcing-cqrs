namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// One row of OrderDetailProjection's projection-private shipment lookup
// (read_models.order_detail_shipments). The three shipment-update events
// (ShipmentDispatched, ShipmentDelivered, ShipmentReturned) carry ShipmentId only,
// and OrderDetail keys on OrderId, so the projection records this
// ShipmentId-to-OrderId mapping from ShipmentScheduled (which carries both) and
// resolves the OrderId on each update event. The mapping persists rather than
// consume-and-delete: a shipment can dispatch, deliver, then return, with the
// follow-on events arriving indefinitely later. Not a public read model. ADR 0020.
public sealed record OrderDetailShipmentRow(
    Guid ShipmentId,
    Guid OrderId,
    DateTime ScheduledUtc);
