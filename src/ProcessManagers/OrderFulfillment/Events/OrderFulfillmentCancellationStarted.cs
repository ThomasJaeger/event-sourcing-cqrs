using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// Compensation begins. Reason is free-form, mirroring CancelOrder.Reason on disk.
// No enum-state change here: the distinct compensation states (ReleasingInventory,
// VoidingPayment) are set by the specific compensation events that follow, so
// they stay distinct rather than collapsing into one Cancelling state.
public sealed record OrderFulfillmentCancellationStarted(string Reason) : IProcessManagerEvent;
