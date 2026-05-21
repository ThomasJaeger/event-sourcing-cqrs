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
public interface IProcessManagerHandler<TInboundEvent>
    where TInboundEvent : IDomainEvent
{
    Task HandleAsync(EventContext<TInboundEvent> context, CancellationToken ct);
}
