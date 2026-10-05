namespace EventSourcingCqrs.Domain.Abstractions;

// Pattern from Chapter 13: exclude live projection writes and other rebuilds while
// resetting and replaying a read model. The lease captures the checkpoint only after
// acquiring the same lock live handlers hold through their mutation and commit.
public interface IProjectionRebuildCoordinator
{
    Task<IProjectionRebuildLease> AcquireAsync(string projectionName, CancellationToken ct);
}

public interface IProjectionRebuildLease : IAsyncDisposable
{
    long Position { get; }

    // Verify the lock's connection is still alive before continuing a separate
    // replay transaction. Losing the lease stops the rebuild with a named failure.
    Task EnsureHeldAsync(CancellationToken ct);
}
