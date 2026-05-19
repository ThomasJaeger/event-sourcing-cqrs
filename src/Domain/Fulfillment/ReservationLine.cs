namespace EventSourcingCqrs.Domain.Fulfillment;

// Inventory's internal-state shape for a reservation against a specific Sales
// line. Entity with LineId carrying identity, parallel to Sales' OrderLine.
// OrderId and LineId travel as raw Guid per ADR 0005; Fulfillment does not
// import Sales' types. Pricing is intentionally absent: pricing is a Sales
// concern. See ADR 0007 and docs/architecture/cross-context-vocabulary.md.
public sealed class ReservationLine(Guid orderId, Guid lineId, string sku, int quantity)
{
    public Guid OrderId { get; } = orderId;
    public Guid LineId { get; } = lineId;
    public string Sku { get; } = sku;
    public int Quantity { get; } = quantity;
}
