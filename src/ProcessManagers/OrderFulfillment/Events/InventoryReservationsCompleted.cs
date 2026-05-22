using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// Every line reserved successfully; the PM moves to scheduling the shipment.
// Partial or total reservation failure routes to compensation instead, which
// records OrderFulfillmentCancellationStarted rather than this event.
public sealed record InventoryReservationsCompleted : IProcessManagerEvent;
