using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Versioning;

// One schema-version link: maps a stored event shape (TFrom) to the next shape (TTo) in its
// lineage. Pattern from Chapter 11: Upcasting. A single link carries no chaining knowledge, only
// its own one-step mapping; EventUpcasterPipeline composes links into a chain and derives the
// version numbers from the chain's topology.
public abstract class Upcaster<TFrom, TTo>
    where TFrom : IDomainEvent
    where TTo : IDomainEvent
{
    public abstract TTo Upcast(TFrom from);

    // The non-generic entry the pipeline binds a delegate to. The cast to TFrom lives here, inside
    // code the derived link already instantiated, so the read path invokes one shape through an
    // IDomainEvent signature without reflection and without the pipeline ever naming the concrete
    // type.
    internal IDomainEvent UpcastUntyped(IDomainEvent from) => Upcast((TFrom)from);
}
