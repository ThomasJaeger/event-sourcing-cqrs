namespace EventSourcingCqrs.Domain.Fulfillment;

// Inventory's internal-state shape for a reservation against a specific Sales
// line. Entity with LineId carrying identity, parallel to Sales' OrderLine.
// Pricing is intentionally absent: pricing is a Sales concern. See ADR 0007.
public sealed class ReservationLine(Guid lineId, string sku, int quantity)
{
    public Guid LineId { get; } = lineId;
    public string Sku { get; } = sku;
    public int Quantity { get; } = quantity;
}
