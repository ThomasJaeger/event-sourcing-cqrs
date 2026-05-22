using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// The PM dispatched RefundPayment. Reason is free-form, mirroring
// RefundPayment.Reason on disk; no PaymentId because the PM already holds it.
public sealed record RefundRequested(string Reason) : IProcessManagerEvent;
