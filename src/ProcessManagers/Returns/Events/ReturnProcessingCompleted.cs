using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.Returns.Events;

// The terminal success event: inventory restocked and payment voided. The Return
// PM's stream is the source of truth for return state; there is no Order-side
// MarkReturned operation (Decision 12).
public sealed record ReturnProcessingCompleted : IProcessManagerEvent;
