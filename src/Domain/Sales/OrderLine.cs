using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales;

public sealed class OrderLine(Guid lineId, string sku, int quantity, Money unitPrice)
{
    public Guid LineId { get; } = lineId;
    public string Sku { get; } = sku;
    public int Quantity { get; } = quantity;
    public Money UnitPrice { get; } = unitPrice;

    public Money Subtotal => new(UnitPrice.Amount * Quantity, UnitPrice.Currency);
}
