using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.Returns.Events;

// The return workflow opened for an order whose shipment was returned. Carries the
// OrderId, which keys the PM stream (pm-return:{orderId:N}) and the downstream
// AdjustInventory and VoidPayment dispatches.
public sealed record ReturnProcessingStarted(Guid OrderId) : IProcessManagerEvent;
