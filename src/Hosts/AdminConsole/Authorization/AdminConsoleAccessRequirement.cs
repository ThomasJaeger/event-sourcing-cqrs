using Microsoft.AspNetCore.Authorization;

namespace EventSourcingCqrs.Hosts.AdminConsole.Authorization;

// Chapter 17: the AdminConsole deny-by-default authorization posture (ADR 0040). The host's fallback
// policy is built on this requirement. AdminConsoleAccessHandler satisfies it by resolving the caller's
// roles from the current-roles read model and asking IPermissionAuthorizer whether those roles grant
// Permission.AccessAdminConsole.
public sealed class AdminConsoleAccessRequirement : IAuthorizationRequirement
{
}
