using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Pipelines;

public class AuthorizationCommandBehaviorTests
{
    // A test-local authorized command pinned to CreateInventory, which the default policy grants to
    // Admin only, so a Customer principal is unauthorized and an Admin principal is authorized.
    private sealed record AdminOnlyCommand : IAuthorizedCommand
    {
        public static Permission RequiredPermission => Permission.CreateInventory;
    }

    private static AuthorizationCommandBehavior<AdminOnlyCommand> Behavior(ICommandContextAccessor accessor) =>
        new(accessor, new PermissionAuthorizer(new RolePermissionRegistry(RolePermissionPolicy.Default)));

    [Fact]
    public async Task An_authorized_principal_reaches_the_handler()
    {
        var accessor = new StubCommandContextAccessor
        {
            Current = new StubCommandContext
            {
                IsAuthenticatedUserDispatch = true,
                Roles = new[] { Role.Admin },
            },
        };
        var nextInvoked = false;

        await Behavior(accessor).HandleAsync(
            new AdminOnlyCommand(),
            () => { nextInvoked = true; return Task.CompletedTask; },
            CancellationToken.None);

        nextInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task An_unauthorized_principal_is_rejected_before_the_handler()
    {
        var accessor = new StubCommandContextAccessor
        {
            Current = new StubCommandContext
            {
                IsAuthenticatedUserDispatch = true,
                Roles = new[] { Role.Customer },
            },
        };
        var nextInvoked = false;

        var act = () => Behavior(accessor).HandleAsync(
            new AdminOnlyCommand(),
            () => { nextInvoked = true; return Task.CompletedTask; },
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedCommandException>()
            .Where(e => e.RequiredPermission == Permission.CreateInventory
                && e.CommandType == typeof(AdminOnlyCommand));
        nextInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task A_non_user_dispatch_passes_through_without_enforcement()
    {
        // The caused, worker, and System paths leave IsAuthenticatedUserDispatch false. Even with no
        // roles the behavior must pass through: enforcement on the caused path would fault the workflow
        // through the dispatch-failure wrapper that catches only domain and concurrency failures.
        var accessor = new StubCommandContextAccessor
        {
            Current = new StubCommandContext
            {
                IsAuthenticatedUserDispatch = false,
                Roles = Array.Empty<Role>(),
            },
        };
        var nextInvoked = false;

        await Behavior(accessor).HandleAsync(
            new AdminOnlyCommand(),
            () => { nextInvoked = true; return Task.CompletedTask; },
            CancellationToken.None);

        nextInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task A_dispatch_with_no_context_passes_through_without_enforcement()
    {
        var accessor = new StubCommandContextAccessor { Current = null };
        var nextInvoked = false;

        await Behavior(accessor).HandleAsync(
            new AdminOnlyCommand(),
            () => { nextInvoked = true; return Task.CompletedTask; },
            CancellationToken.None);

        nextInvoked.Should().BeTrue();
    }
}
