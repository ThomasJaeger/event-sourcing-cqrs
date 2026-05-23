using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.Returns.Events;

// The terminal failure event: a restock or void step failed and the Return PM has
// no compensation, so it halts (Decision 12). Reason is a free-form string naming
// the failing step and, for a partial restock, which lines failed and how many
// were already restocked, so operational tooling can triage. Auto-recovery from
// Stuck is deferred to a future operational-realism pass.
public sealed record ReturnProcessingStuck(string Reason) : IProcessManagerEvent;
