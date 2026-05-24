namespace EventSourcingCqrs.Domain.Abstractions;

// Each bounded context registers the query types it owns through an
// IQueryTypeProvider, parallel to IEventTypeProvider for aggregate events.
// Pull-shape: the provider declares the query types it owns; AddApplication
// walks the providers and registers each into the QueryTypeRegistry. The
// /queries HTTP endpoint dispatches by a type discriminator (ADR 0022), so a
// query's type must be registered for the envelope token to round-trip back to
// a concrete query.
public interface IQueryTypeProvider
{
    IEnumerable<Type> GetQueryTypes();
}
