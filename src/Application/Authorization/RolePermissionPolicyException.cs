namespace EventSourcingCqrs.Application.Authorization;

// Thrown when the role-to-permission policy is incomplete or malformed. The registry validates the
// policy in its constructor, and the composition root constructs the registry, so this surfaces at
// host startup rather than at the first authorization decision.
public sealed class RolePermissionPolicyException : Exception
{
    public RolePermissionPolicyException(string message)
        : base(message)
    {
    }
}
