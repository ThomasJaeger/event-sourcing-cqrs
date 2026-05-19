using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Billing.Events;

public sealed record PaymentCaptured(
    Guid PaymentId,
    Money Amount,
    DateTime CapturedUtc) : IDomainEvent;
