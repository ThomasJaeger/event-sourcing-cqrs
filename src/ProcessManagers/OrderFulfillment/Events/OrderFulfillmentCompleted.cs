using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// The single terminal event, parameterized by outcome. The happy path completes
// with Completed; every compensation branch completes with Cancelled.
public sealed record OrderFulfillmentCompleted(OrderFulfillmentOutcome Outcome) : IProcessManagerEvent;
