using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authorization;

// Authorizes when any of the principal's roles grants the required permission (union semantics).
public sealed class PermissionAuthorizer : IPermissionAuthorizer
{
    private readonly RolePermissionRegistry _registry;

    public PermissionAuthorizer(RolePermissionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public bool IsAuthorized(IReadOnlyCollection<Role> roles, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(roles);

        foreach (var role in roles)
        {
            if (_registry.PermissionsFor(role).Contains(permission))
            {
                return true;
            }
        }

        return false;
    }
}
