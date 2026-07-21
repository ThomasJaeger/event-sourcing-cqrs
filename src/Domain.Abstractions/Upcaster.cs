namespace EventSourcingCqrs.Domain.Abstractions;

// One schema-version link: maps a stored event shape (TFrom) to the next shape (TTo) in its
// lineage. Pattern from Chapter 11: Upcasting. It lives in Domain.Abstractions beside IDomainEvent
// so a domain event's upcaster references only domain types, never the infrastructure seam; the
// EventUpcasterPipeline that composes links into a chain and derives the version numbers from its
// topology stays in the Versioning project. A single link carries no chaining knowledge, only its
// own one-step mapping. It implements the IEventUpcaster marker so a host registers each concrete
// link as that type and a composition root enumerates them for the pipeline.
public abstract class Upcaster<TFrom, TTo> : IEventUpcaster
    where TFrom : IDomainEvent
    where TTo : IDomainEvent
{
    public abstract TTo Upcast(TFrom from);

    // The non-generic entry the pipeline binds a delegate to, reaching this internal across the
    // assembly boundary through the InternalsVisibleTo grant to Versioning. The cast to TFrom lives
    // here, inside code the derived link already instantiated, so the read path invokes one shape
    // through an IDomainEvent signature without reflection and without the pipeline ever naming the
    // concrete type.
    internal IDomainEvent UpcastUntyped(IDomainEvent from) => Upcast((TFrom)from);
}
