using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// Records the observed PaymentAuthorized. A marker: PaymentId and amount are
// already on the stream from OrderFulfillmentStarted, so the transition itself is
// the information.
public sealed record PaymentAuthorizationRecorded : IProcessManagerEvent;
