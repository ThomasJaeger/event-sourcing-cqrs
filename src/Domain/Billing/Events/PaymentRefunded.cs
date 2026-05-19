using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Billing.Events;

public sealed record PaymentRefunded(
    Guid PaymentId,
    Money Amount,
    string Reason,
    DateTime RefundedUtc) : IDomainEvent;
