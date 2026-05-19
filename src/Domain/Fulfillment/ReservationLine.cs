namespace EventSourcingCqrs.Domain.Fulfillment;

// Inventory's internal-state shape for a reservation against a specific Sales
// line. Entity with LineId carrying identity, parallel to Sales' OrderLine.
// OrderId carries through so Release events can raise with the originating
// order's identity for cross-aggregate correlation. Pricing is intentionally
// absent: pricing is a Sales concern. See ADR 0007.
public sealed class ReservationLine(Guid orderId, Guid lineId, string sku, int quantity)
{
    public Guid OrderId { get; } = orderId;
    public Guid LineId { get; } = lineId;
    public string Sku { get; } = sku;
    public int Quantity { get; } = quantity;
}
