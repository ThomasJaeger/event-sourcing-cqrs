using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// One line item of the OrderDetail read model: the C# shape of
// read_models.order_detail_lines, keyed on (OrderId, LineId). Carries the domain's
// Money for the unit price; PostgresOrderDetailStore maps it to the
// unit_price_amount/unit_price_currency pair. Recorded on OrderLineAdded and
// removed on OrderLineRemoved. The Order aggregate forbids adding a currently-live
// LineId but frees it on removal, so a plain insert plus delete never violates the
// primary key, and a re-add after a remove lands the new values cleanly.
public sealed record OrderDetailLineRow(
    Guid OrderId,
    Guid LineId,
    string Sku,
    int Quantity,
    Money UnitPrice);
