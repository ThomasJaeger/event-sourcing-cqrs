using System.Security.Claims;
using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.ReadModels;
using EventSourcingCqrs.Hosts.AdminConsole.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Authorization;

// ADR 0040: the AdminConsole fallback policy is satisfied only when the caller's authoritative roles
// grant Permission.AccessAdminConsole. The handler resolves roles server-side from the current-roles
// read model (keyed on the principal's NameIdentifier), then asks the real IPermissionAuthorizer, so
// these specs drive the handler through HandleAsync over a hand-fed context and assert HasSucceeded.
public class AdminConsoleAccessHandlerTests
{
    private static readonly Guid AdminActor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupportActor = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AdminConsoleAccessHandler Handler(ICurrentUserRolesStore store) =>
        new(store, new PermissionAuthorizer(new RolePermissionRegistry(RolePermissionPolicy.Default)));

    private static AuthorizationHandlerContext Context(Guid actorId)
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, actorId.ToString()) }));
        return new AuthorizationHandlerContext(
            new[] { new AdminConsoleAccessRequirement() }, principal, resource: null);
    }

    [Fact]
    public async Task Admin_actor_satisfies_the_AdminConsole_access_requirement()
    {
        var context = Context(AdminActor);

        await Handler(new FakeCurrentUserRolesStore(AdminActor, Role.Admin)).HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Non_admin_actor_does_not_satisfy_the_AdminConsole_access_requirement()
    {
        var context = Context(SupportActor);

        await Handler(new FakeCurrentUserRolesStore(SupportActor, Role.Support)).HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    // A fake of our own read port (allowed; the no-mocking rule is about backends we do not own). It
    // answers the by-actor-id role lookup and nothing else, since the handler reads roles and never
    // writes them.
    private sealed class FakeCurrentUserRolesStore : ICurrentUserRolesStore
    {
        private readonly Guid _userId;
        private readonly IReadOnlyCollection<Role> _roles;

        public FakeCurrentUserRolesStore(Guid userId, params Role[] roles)
        {
            _userId = userId;
            _roles = roles;
        }

        public Task<IReadOnlyCollection<Role>> GetRolesForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<Role>>(
                userId == _userId ? _roles : Array.Empty<Role>());

        public Task<ICurrentUserRolesUnitOfWork> BeginAsync(CancellationToken ct) =>
            throw new NotSupportedException("The authorization handler reads roles; it never writes them.");

        public Task TruncateAsync(CancellationToken ct) =>
            throw new NotSupportedException("The authorization handler reads roles; it never writes them.");
    }
}
