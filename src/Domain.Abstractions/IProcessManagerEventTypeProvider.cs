namespace EventSourcingCqrs.Domain.Abstractions;

// Each process manager registers its event types through an
// IProcessManagerEventTypeProvider, parallel to IEventTypeProvider for
// aggregates. Pull-shape: the provider declares the PM event types it owns;
// AddPostgresEventStore walks the providers and registers each into the
// ProcessManagerEventTypeRegistry.
public interface IProcessManagerEventTypeProvider
{
    IEnumerable<Type> GetEventTypes();
}
