using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;

namespace EventSourcingCqrs.Domain.Billing;

// Billing bounded context's contribution to the event type registry.
// The four Payment events register in canonical lifecycle order.
public sealed class BillingEventTypeProvider : IEventTypeProvider
{
    public IEnumerable<Type> GetEventTypes() =>
    [
        typeof(PaymentAuthorized),
        typeof(PaymentCaptured),
        typeof(PaymentRefunded),
        typeof(PaymentVoided),
    ];
}
