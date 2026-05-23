using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.ProcessManagers.Returns.Events;

namespace EventSourcingCqrs.ProcessManagers.Returns;

// Registers ReturnProcessManager's five PM-internal event types into the
// ProcessManagerEventTypeRegistry (ADR 0013), parallel to
// OrderFulfillmentEventTypeProvider. WorkersHostFactory registers this one at the
// composition root (commit 27). Listed in workflow order: start, restock, void,
// then the two terminals.
public sealed class ReturnEventTypeProvider : IProcessManagerEventTypeProvider
{
    public IEnumerable<Type> GetEventTypes() =>
    [
        typeof(ReturnProcessingStarted),
        typeof(InventoryRestockRecorded),
        typeof(PaymentVoidRecorded),
        typeof(ReturnProcessingCompleted),
        typeof(ReturnProcessingStuck),
    ];
}
