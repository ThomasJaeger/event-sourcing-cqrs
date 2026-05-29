using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Access.ReadModels;

// The current-roles read model's persistence port. The identity-establishment commit (Phase 9)
// loads a user's roles through GetRolesForUserAsync to build the principal. Lives in
// Domain.Access.ReadModels alongside the context that derives it (ADR 0008), so the Infrastructure
// adapter references Domain rather than Application (which would invert the layering rule ADR 0026
// records).
public interface ICurrentUserRolesStore
{
    // Opens a unit of work. A handler applies its role change on the unit of work, then commits,
    // which advances the checkpoint in the same transaction.
    Task<ICurrentUserRolesUnitOfWork> BeginAsync(CancellationToken ct);

    // Read path: the principal factory reads a user's current roles.
    Task<IReadOnlyCollection<Role>> GetRolesForUserAsync(Guid userId, CancellationToken ct);

    // Rebuild support: drop every row so a replay starts empty.
    Task TruncateAsync(CancellationToken ct);
}
