using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Sales.Events;

public sealed record OrderDrafted(
    Guid OrderId,
    Guid CustomerId,
    DateTime DraftedUtc,
    string Channel) : IDomainEvent
{
    // The channel stamped on a draft whose stored row predates the member. OrderDraftedV1ToV2 fills
    // it when it lifts a version-1 row; a real draft carries the composing host's entry channel.
    public const string UnknownChannel = "unknown";
}
