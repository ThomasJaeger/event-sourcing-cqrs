using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.ReadModels;

namespace EventSourcingCqrs.Projections.Tests;

// In-memory ICurrentUserRolesStore for CurrentRolesProjectionTests. Writes apply immediately to the
// backing set and CommitAsync records the checkpoint, because the projection always commits and the
// tests assert on the committed result. Rollback is exercised against the real database in the
// Postgres adapter tests. Mirrors InMemorySkuToInventoryIdStore, minus the notification staging the
// current-roles unit of work does not carry.
internal sealed class InMemoryCurrentUserRolesStore : ICurrentUserRolesStore
{
    private readonly HashSet<(Guid UserId, Role Role)> _assignments = [];

    // Exposed so tests can assert the checkpoint advanced with the write.
    public Dictionary<string, long> Checkpoints { get; } = [];

    public Task<ICurrentUserRolesUnitOfWork> BeginAsync(CancellationToken ct)
        => Task.FromResult<ICurrentUserRolesUnitOfWork>(new UnitOfWork(this));

    public Task<IReadOnlyCollection<Role>> GetRolesForUserAsync(Guid userId, CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<Role>>(
            _assignments.Where(a => a.UserId == userId).Select(a => a.Role).ToList());

    public Task TruncateAsync(CancellationToken ct)
    {
        _assignments.Clear();
        return Task.CompletedTask;
    }

    private sealed class UnitOfWork(InMemoryCurrentUserRolesStore store) : ICurrentUserRolesUnitOfWork
    {
        public Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(store.Checkpoints.GetValueOrDefault(projectionName));

        public Task AssignAsync(Guid userId, Role role, CancellationToken ct)
        {
            // ON CONFLICT DO NOTHING: a redelivered assignment leaves the set unchanged.
            store._assignments.Add((userId, role));
            return Task.CompletedTask;
        }

        public Task RevokeAsync(Guid userId, Role role, CancellationToken ct)
        {
            store._assignments.Remove((userId, role));
            return Task.CompletedTask;
        }

        public Task CommitAsync(string projectionName, long position, CancellationToken ct)
        {
            // GREATEST, mirroring PostgresCheckpointStore's UPSERT.
            store.Checkpoints[projectionName] = Math.Max(
                store.Checkpoints.GetValueOrDefault(projectionName), position);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
