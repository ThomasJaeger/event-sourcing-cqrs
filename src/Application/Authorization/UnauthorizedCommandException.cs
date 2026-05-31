namespace EventSourcingCqrs.Application.Authorization;

// Thrown by the authorization behavior when an authenticated principal lacks the permission a command
// requires. The Api host's exception-mapping middleware maps it to 403. Carries the command type and
// the required permission so the boundary logs the denial without re-deriving either. A runtime
// per-command decision, distinct from the startup-time RolePermissionPolicyException.
public sealed class UnauthorizedCommandException : Exception
{
    public Type CommandType { get; }
    public Permission RequiredPermission { get; }

    public UnauthorizedCommandException(Type commandType, Permission requiredPermission)
        : base($"The principal is not authorized to dispatch '{commandType.Name}'. " +
               $"It requires permission '{requiredPermission}'.")
    {
        CommandType = commandType;
        RequiredPermission = requiredPermission;
    }
}
