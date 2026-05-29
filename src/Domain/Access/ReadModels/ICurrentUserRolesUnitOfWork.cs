using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Access.ReadModels;

// One projection write against the current-roles read model: the role-row change and the checkpoint
// advance run on one transaction, committed together. CommitAsync makes them durable; DisposeAsync
// without a commit rolls them back.
//
// No PublishOnCommit member, unlike the six dashboard-feeding units of work: the current-roles read
// model feeds the principal factory, not a live dashboard, so it has no SignalR subscriber and
// stages no notification (born-at-consumer).
public interface ICurrentUserRolesUnitOfWork : IAsyncDisposable
{
    // Reads the projection's checkpoint inside this unit of work's transaction, so the idempotency
    // check shares isolation with the write.
    Task<long> GetCheckpointAsync(string projectionName, CancellationToken ct);

    // RoleAssigned: record that the user holds the role. Idempotent on redelivery.
    Task AssignAsync(Guid userId, Role role, CancellationToken ct);

    // RoleRevoked: remove the role from the user. A no-op if the row is absent.
    Task RevokeAsync(Guid userId, Role role, CancellationToken ct);

    // Advances the checkpoint to `position` and commits, both in the transaction the write ran in.
    Task CommitAsync(string projectionName, long position, CancellationToken ct);
}
