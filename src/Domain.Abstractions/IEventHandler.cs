namespace EventSourcingCqrs.Domain.Abstractions;

// Invariant in TEvent, not contravariant. The previous IEventHandler<in TEvent>
// could not keep the `in` once HandleAsync takes EventContext<TEvent>:
// EventContext<>'s positional Event property is init-settable, so EventContext<>
// is invariant in its own type parameter, and the compiler rejects passing a
// contravariant TEvent to it (CS1961). The `in` bought nothing anyway, since
// InProcessMessageDispatcher resolves the exact closed handler type.
public interface IEventHandler<TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(EventContext<TEvent> context, CancellationToken ct);
}
