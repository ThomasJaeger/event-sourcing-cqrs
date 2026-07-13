using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.EventStore.ContractTests;

// The suite's own event types. They live here rather than in any adapter's test kit because
// the suite must stay ignorant of who implements IEventStore. A backend registers these types
// into whatever type registry its engine uses; that translation is the backend's job.
public sealed record ContractOrderPlaced(Guid OrderId, decimal Total) : IDomainEvent;

public sealed record ContractOrderNoted(string Note) : IDomainEvent;

public sealed record ContractStepRecorded(int Step) : IProcessManagerEvent;

// What a backend has to register before the suite can run against it.
public static class ContractEventTypes
{
    public static IReadOnlyList<Type> DomainEvents { get; } =
        [typeof(ContractOrderPlaced), typeof(ContractOrderNoted)];

    public static IReadOnlyList<Type> ProcessManagerEvents { get; } =
        [typeof(ContractStepRecorded)];
}
