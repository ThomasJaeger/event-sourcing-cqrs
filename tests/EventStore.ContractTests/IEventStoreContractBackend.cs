using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.EventStore.ContractTests;

// What the universal suite needs from an adapter in order to run against a real backend. One
// instance per fact: the store and the position hook address the same isolated backend, and
// disposing it tears that backend down.
//
// ReadCommittedPositionsAsync reaches past IEventStore because the commit-order and gap-tolerance
// probes observe the engine's position sequence directly, including the PM rows ReadAllAsync
// excludes, and that cannot be seen through the port alone. Engines whose store also exposes an
// interactive, held-open append implement the narrower IHeldWriterContractBackend for the
// held-writer probes; every engine implements this one.
public interface IEventStoreContractBackend : IAsyncDisposable
{
    // The port under test, composed against a real backend with the suite's event types
    // registered.
    IEventStore Store { get; }

    // Every committed position in the engine's event feed, ascending, including the rows
    // ReadAllAsync excludes. Both append paths draw from one position sequence, so a probe
    // blind to PM rows would be blind to half the contract.
    Task<IReadOnlyList<long>> ReadCommittedPositionsAsync();
}
