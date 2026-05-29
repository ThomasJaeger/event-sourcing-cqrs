using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.Events;

namespace EventSourcingCqrs.Domain.Access;

// The set of roles a single user holds, event-sourced as its own stream keyed by the user id. The
// Access context is the auditable record of who was granted what: every assignment and revocation
// flows through the event store. This is the write side; roles are checked at runtime off the
// principal, which the current-roles read model builds.
public sealed class UserRoles : AggregateRoot
{
    private readonly HashSet<Role> _roles = [];

    // Public for event-sourced rehydration. Use UserRoles.Create(...) to open a user's stream.
    public UserRoles()
    {
    }

    public IReadOnlyCollection<Role> Roles => _roles;

    // Opens a user's role stream by granting the first role. The aggregate id is the user id, so the
    // stream is one per user.
    public static UserRoles Create(Guid userId, Role role)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException("Cannot assign a role to an empty user id.");
        }
        var userRoles = new UserRoles();
        userRoles.Raise(new RoleAssigned(userId, role));
        return userRoles;
    }

    public void Assign(Role role)
    {
        if (_roles.Contains(role))
        {
            throw new DomainException($"User {Id} already holds role {role}.");
        }
        Raise(new RoleAssigned(Id, role));
    }

    public void Revoke(Role role)
    {
        if (!_roles.Contains(role))
        {
            throw new DomainException($"User {Id} does not hold role {role}.");
        }
        Raise(new RoleRevoked(Id, role));
    }

    protected override void Apply(IDomainEvent @event)
    {
        switch (@event)
        {
            case RoleAssigned e:
                Id = e.UserId;
                _roles.Add(e.Role);
                break;

            case RoleRevoked e:
                Id = e.UserId;
                _roles.Remove(e.Role);
                break;

            default:
                throw new InvalidOperationException(
                    $"UserRoles does not handle event type {@event.GetType().Name}.");
        }
    }
}
