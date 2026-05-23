using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// The PM dispatched VoidPayment. The OrderFulfillment workflow never captures the
// payment, so a compensating reversal voids the authorization rather than
// refunding a capture (Payment voids from Authorized, refunds from Captured).
// Reason is free-form, mirroring VoidPayment.Reason on disk; no PaymentId because
// the PM already holds it.
public sealed record PaymentVoidRequested(string Reason) : IProcessManagerEvent;
