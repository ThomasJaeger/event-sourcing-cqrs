using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Sales.Events;

// The version-1 shape of OrderDrafted, before it carried a channel. This record exists solely as the
// stored shape of rows an older revision wrote: it is never raised, never registered in a type
// registry, and never read except by OrderDraftedV1ToV2, which lifts it to the current OrderDrafted.
// The storage name "OrderDrafted" stays the terminal's, so a stored v1 row and a current row share it
// and the version column tells them apart. Pattern from Chapter 11: Upcasting.
public sealed record OrderDraftedV1(
    Guid OrderId,
    Guid CustomerId,
    DateTime DraftedUtc) : IDomainEvent;
