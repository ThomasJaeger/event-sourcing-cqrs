using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// The PM starts on OrderPlaced and dispatches AuthorizePayment with a PM-chosen
// PaymentId, recorded here so a later RefundPayment can name it. OrderTotal is
// forensic seed: a reader of the PM stream sees the amount being orchestrated
// without joining to the Order stream. No CustomerId (no PM consumer, recoverable
// from the Order stream) and no timestamp (envelope metadata carries OccurredUtc).
public sealed record OrderFulfillmentStarted(
    Guid OrderId,
    Money OrderTotal,
    Guid PaymentId) : IProcessManagerEvent;
