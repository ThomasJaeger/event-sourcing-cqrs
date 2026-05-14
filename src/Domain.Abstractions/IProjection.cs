namespace EventSourcingCqrs.Domain.Abstractions;

// Marker for a projection. Name pins the projection's checkpoint key once, so
// handler call sites reference Name rather than a repeated string literal. A
// projection class implements this plus one IEventHandler<TEvent> per event
// type it handles. The marker also lets the replayer and the AdminConsole
// projection-status dashboard enumerate projections from DI.
public interface IProjection
{
    string Name { get; }
}
