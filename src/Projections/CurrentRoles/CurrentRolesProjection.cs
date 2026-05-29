using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.Events;
using EventSourcingCqrs.Domain.Access.ReadModels;

namespace EventSourcingCqrs.Projections.CurrentRoles;

// Pattern from Chapter 13: projects the Access events into the current-roles-per-user read model
// the principal factory reads. Idempotent under replay: each handler reads the checkpoint inside its
// write transaction and skips events at or below it, so an at-least-once redelivery is a no-op.
public sealed class CurrentRolesProjection
    : IProjection,
      IEventHandler<RoleAssigned>,
      IEventHandler<RoleRevoked>
{
    public string Name => "current-roles";

    private readonly ICurrentUserRolesStore _store;

    public CurrentRolesProjection(ICurrentUserRolesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task HandleAsync(EventContext<RoleAssigned> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        await uow.AssignAsync(context.Event.UserId, context.Event.Role, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }

    public async Task HandleAsync(EventContext<RoleRevoked> context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var uow = await _store.BeginAsync(ct);
        if (await uow.GetCheckpointAsync(Name, ct) >= context.GlobalPosition)
        {
            return;
        }
        await uow.RevokeAsync(context.Event.UserId, context.Event.Role, ct);
        await uow.CommitAsync(Name, context.GlobalPosition, ct);
    }
}
