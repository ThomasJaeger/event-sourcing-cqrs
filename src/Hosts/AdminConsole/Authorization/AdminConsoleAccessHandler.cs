using System.Security.Claims;
using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Access.ReadModels;
using Microsoft.AspNetCore.Authorization;

namespace EventSourcingCqrs.Hosts.AdminConsole.Authorization;

// Chapter 17: the AdminConsole deny-by-default authorization handler (ADR 0040). It satisfies the
// fallback requirement only when the caller's authoritative roles grant console access. Roles come
// from the current-roles read model keyed on the authenticated actor, never from a principal claim,
// so a stale or forged role assertion cannot widen access.
public sealed class AdminConsoleAccessHandler : AuthorizationHandler<AdminConsoleAccessRequirement>
{
    private readonly ICurrentUserRolesStore _store;
    private readonly IPermissionAuthorizer _authorizer;

    public AdminConsoleAccessHandler(ICurrentUserRolesStore store, IPermissionAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorizer);
        _store = store;
        _authorizer = authorizer;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminConsoleAccessRequirement requirement)
    {
        var nameIdentifier = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(nameIdentifier, out var actorId))
        {
            // Deny-by-default: a principal with no parseable actor id resolves to no roles, so it never
            // succeeds the requirement and the fallback policy denies.
            return;
        }

        var roles = await _store.GetRolesForUserAsync(actorId, CancellationToken.None);
        if (_authorizer.IsAuthorized(roles, Permission.AccessAdminConsole))
        {
            context.Succeed(requirement);
        }
    }
}
