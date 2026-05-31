using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Pipelines;

public class AuthorizationQueryBehaviorTests
{
    // A test-local authorized query pinned to ViewInventory, which the default policy grants to Support
    // and Admin but not Customer, so a Customer principal is unauthorized and an Admin principal is
    // authorized.
    private sealed record InventoryOnlyQuery : IQuery<string>, IAuthorizedQuery
    {
        public static Permission RequiredPermission => Permission.ViewInventory;
    }

    private static AuthorizationQueryBehavior<InventoryOnlyQuery, string> Behavior(IQueryContextAccessor accessor) =>
        new(accessor, new PermissionAuthorizer(new RolePermissionRegistry(RolePermissionPolicy.Default)));

    [Fact]
    public async Task An_authorized_principal_reaches_the_handler()
    {
        var accessor = new StubQueryContextAccessor
        {
            Current = new StubQueryContext
            {
                IsAuthenticatedUserQuery = true,
                Roles = new[] { Role.Admin },
            },
        };
        var nextInvoked = false;

        await Behavior(accessor).HandleAsync(
            new InventoryOnlyQuery(),
            () => { nextInvoked = true; return Task.FromResult("ok"); },
            CancellationToken.None);

        nextInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task An_unauthorized_principal_is_rejected_before_the_handler()
    {
        var accessor = new StubQueryContextAccessor
        {
            Current = new StubQueryContext
            {
                IsAuthenticatedUserQuery = true,
                Roles = new[] { Role.Customer },
            },
        };
        var nextInvoked = false;

        var act = () => Behavior(accessor).HandleAsync(
            new InventoryOnlyQuery(),
            () => { nextInvoked = true; return Task.FromResult("ok"); },
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedQueryException>()
            .Where(e => e.RequiredPermission == Permission.ViewInventory
                && e.QueryType == typeof(InventoryOnlyQuery));
        nextInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task A_non_authenticated_query_passes_through_without_enforcement()
    {
        // The bare AskAsync path leaves IsAuthenticatedUserQuery false. Even with no roles the behavior
        // passes through, the same gate discipline the command behavior uses.
        var accessor = new StubQueryContextAccessor
        {
            Current = new StubQueryContext
            {
                IsAuthenticatedUserQuery = false,
                Roles = Array.Empty<Role>(),
            },
        };
        var nextInvoked = false;

        await Behavior(accessor).HandleAsync(
            new InventoryOnlyQuery(),
            () => { nextInvoked = true; return Task.FromResult("ok"); },
            CancellationToken.None);

        nextInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task A_query_with_no_context_passes_through_without_enforcement()
    {
        var accessor = new StubQueryContextAccessor { Current = null };
        var nextInvoked = false;

        await Behavior(accessor).HandleAsync(
            new InventoryOnlyQuery(),
            () => { nextInvoked = true; return Task.FromResult("ok"); },
            CancellationToken.None);

        nextInvoked.Should().BeTrue();
    }
}
