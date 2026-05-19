namespace EventSourcingCqrs.Domain.Fulfillment;

// Shipment's per-line shape, carrying the originating Order's LineId so
// downstream compensation can refer back to a specific Order line. Entity
// with LineId carrying identity, parallel to ReservationLine. Pricing is
// intentionally absent: pricing is a Sales concern. See ADR 0007.
public sealed class ShipmentLine(Guid orderId, Guid lineId, string sku, int quantity)
{
    public Guid OrderId { get; } = orderId;
    public Guid LineId { get; } = lineId;
    public string Sku { get; } = sku;
    public int Quantity { get; } = quantity;
}
