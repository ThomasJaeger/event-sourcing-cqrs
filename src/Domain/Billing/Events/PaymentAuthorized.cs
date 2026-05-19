using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Billing.Events;

// OrderId is Sales' identifier travelling into Billing as a raw Guid (ADR
// 0005). Amount uses the shared-kernel Money type (ADR 0006). Together they
// express the Customer-Supplier pattern from Chapter 7: Sales publishes,
// Billing consumes, no Sales type imports. See
// docs/architecture/cross-context-vocabulary.md.
public sealed record PaymentAuthorized(
    Guid PaymentId,
    Guid OrderId,
    Money Amount,
    string PaymentMethodReference,
    DateTime AuthorizedUtc) : IDomainEvent;
