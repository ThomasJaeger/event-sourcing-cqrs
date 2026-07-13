using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.EventStore.ContractTests;

// What the suite needs from an adapter in order to run against a real backend. One instance
// per fact: the store and both hooks address the same isolated backend, and disposing it tears
// that backend down.
//
// The two hooks exist because the divergence probes have to reach past IEventStore. Commit
// ordering and gap tolerance are properties of the engine's position sequence, and neither can
// be observed through the port alone. An engine that cannot supply these hooks cannot be shown
// to hold ADR 0044, which is the point of asking for them.
public interface IEventStoreContractBackend : IAsyncDisposable
{
    // The port under test, composed against a real backend with the suite's event types
    // registered.
    IEventStore Store { get; }

    // Opens a transaction that participates in the engine's append serialization point, draws a
    // global position, and stays open until told to commit or roll back. Between its INSERT and
    // its COMMIT, a real append looks exactly like this from the outside.
    Task<IHeldWriter> StartHeldWriterAsync(StreamId streamId);

    // Every committed position in the engine's event feed, ascending, including the rows
    // ReadAllAsync excludes. Both append paths draw from one position sequence, so a probe
    // blind to PM rows would be blind to half the contract.
    Task<IReadOnlyList<long>> ReadCommittedPositionsAsync();
}

// A writer parked mid-transaction, holding a drawn-but-uncommitted global position.
public interface IHeldWriter : IAsyncDisposable
{
    Task CommitAsync();

    // Rolling back throws the drawn position away. Position assignment does not roll back with
    // the transaction, so the resulting gap is permanent (ADR 0044).
    Task RollbackAsync();
}
