using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Billing.Events;

public sealed record PaymentAuthorized(
    Guid PaymentId,
    Guid OrderId,
    Money Amount,
    string PaymentMethodReference,
    DateTime AuthorizedUtc) : IDomainEvent;
