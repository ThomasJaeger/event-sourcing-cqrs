using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.Returns.Events;

// The PM dispatched VoidPayment for the returned order. Void rather than refund:
// the OrderFulfillment workflow never captures the payment, so it is still
// Authorized at return time (Payment voids from Authorized, refunds from
// Captured). Same void-not-refund correction as the OrderFulfillment PM.
public sealed record PaymentVoidRecorded : IProcessManagerEvent;
