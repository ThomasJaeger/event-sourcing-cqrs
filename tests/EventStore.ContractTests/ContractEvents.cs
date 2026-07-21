using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;

namespace EventSourcingCqrs.EventStore.ContractTests;

// The suite's own event types. They live here rather than in any adapter's test kit because
// the suite must stay ignorant of who implements IEventStore. A backend registers these types
// into whatever type registry its engine uses; that translation is the backend's job.
public sealed record ContractOrderPlaced(Guid OrderId, decimal Total) : IDomainEvent;

public sealed record ContractOrderNoted(string Note) : IDomainEvent;

public sealed record ContractStepRecorded(int Step) : IProcessManagerEvent;

// A two-version lineage the suite uses to drive upcast-on-read across every engine (Chapter 11:
// Upcasting). ContractItemArchivedV1 is the shape an older revision of the code wrote; the current
// shape adds one member, Revision, that the upcaster fills with a default when it lifts a stored V1
// event forward. Only the terminal is registered, so "ContractItemArchived" is the stable storage
// name every version is stored under; a V1 row is a historical artifact, not something the current
// append path can produce (it only ever writes the current shape). The upcaster sits beside the
// records because the storage name is a versioning decision, not an engine decision, so this lineage
// is engine-agnostic the way the rest of this file is.
public sealed record ContractItemArchivedV1(Guid ItemId) : IDomainEvent;

public sealed record ContractItemArchived(Guid ItemId, int Revision) : IDomainEvent;

public sealed class ContractItemArchivedV1ToV2 : Upcaster<ContractItemArchivedV1, ContractItemArchived>
{
    public override ContractItemArchived Upcast(ContractItemArchivedV1 from)
        => new(from.ItemId, Revision: 1);
}

// What a backend has to register before the suite can run against it. The terminal
// ContractItemArchived joins the list; the V1 shape does not, because only the current type is
// registered and the upcaster reaches the older one through the chain.
public static class ContractEventTypes
{
    public static IReadOnlyList<Type> DomainEvents { get; } =
        [typeof(ContractOrderPlaced), typeof(ContractOrderNoted), typeof(ContractItemArchived)];

    public static IReadOnlyList<Type> ProcessManagerEvents { get; } =
        [typeof(ContractStepRecorded)];
}
