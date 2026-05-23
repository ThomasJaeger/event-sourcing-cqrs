namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// One row of OrderDetailProjection's projection-private payment lookup
// (read_models.order_detail_payments). Three of the four payment events
// (PaymentCaptured, PaymentRefunded, PaymentVoided) carry PaymentId only, and
// OrderDetail keys on OrderId, so the projection records this PaymentId-to-OrderId
// mapping from PaymentAuthorized (which carries both) and resolves the OrderId on
// each follow-on event. The mapping persists rather than consume-and-delete: a
// payment can capture then refund, with the follow-on events arriving indefinitely
// later. Same rule and shape as the shipment lookup. Not a public read model. ADR 0020.
public sealed record OrderDetailPaymentRow(
    Guid PaymentId,
    Guid OrderId,
    DateTime AuthorizedUtc);
