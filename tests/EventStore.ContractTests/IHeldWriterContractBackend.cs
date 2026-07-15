using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.EventStore.ContractTests;

// The narrower seam for engines whose store exposes an interactive, held-open append. It adds the
// held-writer hook to the universal backend, and the held-writer contract facts draw their backend
// from it. An engine that cannot park an append mid-transaction does not implement this and does
// not run those facts; the universal facts it still owes are unaffected.
public interface IHeldWriterContractBackend : IEventStoreContractBackend
{
    // Opens a transaction that participates in the engine's append serialization point, draws a
    // global position, and stays open until told to commit or roll back. Between its INSERT and
    // its COMMIT, a real append looks exactly like this from the outside.
    Task<IHeldWriter> StartHeldWriterAsync(StreamId streamId);
}

// A writer parked mid-transaction, holding a drawn-but-uncommitted global position.
public interface IHeldWriter : IAsyncDisposable
{
    Task CommitAsync();

    // Rolling back throws the drawn position away. Position assignment does not roll back with
    // the transaction, so the resulting gap is permanent (ADR 0044).
    Task RollbackAsync();
}
