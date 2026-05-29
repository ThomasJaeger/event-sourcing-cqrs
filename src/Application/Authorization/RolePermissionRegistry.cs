using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authorization;

// Resolves a role's permission set from a validated policy. Validation runs in the constructor so a
// gap is a loud startup failure: every role maps to a set, and every granted permission is a defined
// member of the enumeration. A concrete sealed class injected directly, the way the type registries
// are, since no consumer needs to swap the policy source.
public sealed class RolePermissionRegistry
{
    private readonly IReadOnlyDictionary<Role, IReadOnlySet<Permission>> _policy;

    public RolePermissionRegistry(IReadOnlyDictionary<Role, IReadOnlySet<Permission>> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        foreach (var role in Enum.GetValues<Role>())
        {
            if (!policy.TryGetValue(role, out var permissions))
            {
                throw new RolePermissionPolicyException(
                    $"Role-to-permission policy has no entry for role '{role}'. Every role must map to a permission set.");
            }

            foreach (var permission in permissions)
            {
                if (!Enum.IsDefined(permission))
                {
                    throw new RolePermissionPolicyException(
                        $"Role-to-permission policy grants an undefined permission to role '{role}'. Permission values must be defined members of the Permission enumeration.");
                }
            }
        }

        _policy = policy;
    }

    public IReadOnlySet<Permission> PermissionsFor(Role role)
    {
        if (!_policy.TryGetValue(role, out var permissions))
        {
            throw new RolePermissionPolicyException(
                $"Role-to-permission policy has no entry for role '{role}'.");
        }

        return permissions;
    }
}
