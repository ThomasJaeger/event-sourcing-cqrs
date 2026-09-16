using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authorization;

internal static class OrderCommandAuthorization
{
    public static void EnsureOwnership<TCommand>(Guid customerId, ICommandContext? context)
        where TCommand : IAuthorizedCommand
    {
        if (context is not { AuthorizationMode: DispatchAuthorizationMode.AuthenticatedUser })
            return;
        if (context.Roles.Any(role => RolePermissionPolicy.Default.TryGetValue(role, out var permissions)
            && permissions.Contains(Permission.ViewCustomer)))
            return;
        if (customerId != context.ActorId)
            throw new UnauthorizedCommandException(typeof(TCommand), TCommand.RequiredPermission);
    }
}
