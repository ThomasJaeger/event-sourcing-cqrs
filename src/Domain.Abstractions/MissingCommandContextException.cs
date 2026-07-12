namespace EventSourcingCqrs.Domain.Abstractions;

// Thrown when a process manager saves events with no command context in flight. Every production
// route into a PM save carries one: the outbox dispatcher establishes a caused-event context per
// handler (ADR 0042), and the delay queue resurfaces a timeout through the command pipeline, which
// establishes its own. A save that finds none is a dispatch-wiring regression. Standalone and named
// (not a DomainException subclass, which is the aggregate-rejection type) so the write path fails
// closed here rather than stamping the PM's events with an empty correlation, causation, and actor,
// which would drop them out of the workflow's trace behind rows that look written.
public sealed class MissingCommandContextException : Exception
{
    public MissingCommandContextException()
        : base("A process manager saved events with no command context in flight. The outbox " +
               "dispatcher establishes one per handler and the command pipeline establishes one for " +
               "a resurfaced timeout; a missing context here is a dispatch-wiring regression.")
    {
    }
}
