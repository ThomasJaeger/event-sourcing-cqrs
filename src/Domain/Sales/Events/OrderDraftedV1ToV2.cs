using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Sales.Events;

// Lifts a stored version-1 OrderDrafted to the current shape, defaulting the channel a v1 row could
// not carry to OrderDrafted.UnknownChannel. The upcaster base lives in Domain.Abstractions, so this
// domain lineage references only domain types. Pattern from Chapter 11: Upcasting.
public sealed class OrderDraftedV1ToV2 : Upcaster<OrderDraftedV1, OrderDrafted>
{
    public override OrderDrafted Upcast(OrderDraftedV1 from)
        => new(from.OrderId, from.CustomerId, from.DraftedUtc, OrderDrafted.UnknownChannel);
}
