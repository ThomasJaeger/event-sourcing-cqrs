namespace EventSourcingCqrs.Domain.Abstractions;

// Each bounded context registers its events through an IEventTypeProvider.
// Pull-shape: the provider declares the types it owns; a registry consumer
// (AddPostgresEventStore) walks the providers and registers each type.
// Domain.Abstractions stays independent of any concrete registry, and one
// registry implementation can consume providers from any number of contexts.
public interface IEventTypeProvider
{
    IEnumerable<Type> GetEventTypes();
}
