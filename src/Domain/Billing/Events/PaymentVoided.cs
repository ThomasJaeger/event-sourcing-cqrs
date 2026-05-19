using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Billing.Events;

public sealed record PaymentVoided(
    Guid PaymentId,
    string Reason,
    DateTime VoidedUtc) : IDomainEvent;
