namespace EventSourcingCqrs.Application.Authorization;

// Thrown by the query-authorization behavior when an authenticated principal lacks the permission a
// query requires. The Api host's exception-mapping middleware maps it to 403, alongside the command
// twin UnauthorizedCommandException. Carries the query type and the required permission so the boundary
// logs the denial without re-deriving either. A runtime per-query decision, distinct from the
// startup-time RolePermissionPolicyException.
public sealed class UnauthorizedQueryException : Exception
{
    public Type QueryType { get; }
    public Permission RequiredPermission { get; }

    public UnauthorizedQueryException(Type queryType, Permission requiredPermission)
        : base($"The principal is not authorized to run '{queryType.Name}'. " +
               $"It requires permission '{requiredPermission}'.")
    {
        QueryType = queryType;
        RequiredPermission = requiredPermission;
    }
}
