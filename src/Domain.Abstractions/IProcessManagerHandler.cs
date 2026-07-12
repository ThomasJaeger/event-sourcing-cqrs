namespace EventSourcingCqrs.Domain.Abstractions;

// The consumer-side contract for an event a process manager subscribes to,
// parallel to IEventHandler<TEvent> for projections. Same
// EventContext<TInboundEvent> signature (ADR 0010) so projections and process
// managers share one dispatch convention, one test-harness shape, and one DI
// registration pattern; the dispatcher routes by interface so the two consumer
// kinds keep separate failure semantics (ADR 0015).
//
// Invariant in TInboundEvent for the same reason IEventHandler is: EventContext<>
// has an init-settable Event property and is therefore invariant in its own
// type parameter, so the handler cannot be contravariant in the event type.
public interface IProcessManagerHandler<TInboundEvent> : IProcessManagerHandler
    where TInboundEvent : IDomainEvent
{
    Task HandleAsync(EventContext<TInboundEvent> context, CancellationToken ct);
}

// The identity a process manager writes and dispatches under, declared on the handler rather than
// held privately, so the outbox dispatcher can read it off the handler it is about to invoke and
// establish that handler's caused-event context (ADR 0042). Non-generic because the dispatcher
// resolves handlers as object out of the container and would otherwise need reflection to reach it.
//
// Per handler, not per message: two process managers can subscribe to one event, and each writes
// under its own actor.
public interface IProcessManagerHandler
{
    SystemActor Actor { get; }
}
