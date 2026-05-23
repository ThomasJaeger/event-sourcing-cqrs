using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// The header row of the OrderDetail read model: the C# shape of
// read_models.order_detail. Carries the domain's OrderStatus, Money, and Address
// directly; PostgresOrderDetailStore maps Status to text, Total to the
// total_amount/total_currency pair, and ShippingAddress to the four
// shipping_address_* columns. Status is the last-observed lifecycle label (D6).
// The optional fields are null until the event that sets them is observed: Total
// until OrderPlaced, ShippingAddress until ShippingAddressSet, and each *Utc until
// its lifecycle event. ReturnedUtc is the Fulfillment return state composed onto
// the header (D5); a return is not a Sales status, so it has no status value.
public sealed record OrderDetailRow(
    Guid OrderId,
    Guid CustomerId,
    OrderStatus Status,
    DateTime? PlacedUtc,
    DateTime? ShippedUtc,
    DateTime? CancelledUtc,
    DateTime? CompletedUtc,
    DateTime? ReturnedUtc,
    Money? Total,
    Address? ShippingAddress,
    DateTime LastUpdatedUtc);
