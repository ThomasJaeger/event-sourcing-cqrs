using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.SharedKernel;
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
                AuthorizationMode = DispatchAuthorizationMode.AuthenticatedUser,
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
                AuthorizationMode = DispatchAuthorizationMode.AuthenticatedUser,
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
    public async Task A_None_mode_dispatch_passes_through_without_enforcement()
    {
        // None is the unenforced default for the worker and bare paths. Even with no roles the behavior
        // passes through without probing the authorizer; enforcement here would fault those internal
        // dispatches through the wrapper that catches only domain and concurrency failures.
        var accessor = new StubCommandContextAccessor
        {
            Current = new StubCommandContext
            {
                AuthorizationMode = DispatchAuthorizationMode.None,
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

    [Fact]
    public async Task Every_caused_command_authorizes_under_the_system_role()
    {
        // The drift guard: the eight commands the process managers dispatch on the caused path each
        // authorize under Role.System. If a caused command's required permission ever leaves the System
        // role's set, this fails here rather than surfacing only at runtime as a faulted workflow.
        await AssertSystemActorAuthorizes(
            new AuthorizePayment(Guid.NewGuid(), Guid.NewGuid(), new Money(1m, Currency.USD), "auth-ref"));
        await AssertSystemActorAuthorizes(
            new ReserveInventory(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1));
        await AssertSystemActorAuthorizes(
            new ReleaseInventory(Guid.NewGuid(), Guid.NewGuid(), "compensating release"));
        await AssertSystemActorAuthorizes(
            new ScheduleShipment(
                Guid.NewGuid(), Guid.NewGuid(),
                new Address("1 Main St", "Smalltown", "12345", "US"),
                [new ShipmentLine(Guid.NewGuid(), Guid.NewGuid(), "SKU-1", 1)]));
        await AssertSystemActorAuthorizes(new MarkOrderCompleted(Guid.NewGuid()));
        await AssertSystemActorAuthorizes(
            new CancelOrder(Guid.NewGuid(), "compensating cancel", Guid.NewGuid()));
        await AssertSystemActorAuthorizes(new VoidPayment(Guid.NewGuid(), "compensating void"));
        await AssertSystemActorAuthorizes(new AdjustInventory(Guid.NewGuid(), 1, "restock on return"));
    }

    [Fact]
    public async Task A_command_outside_the_system_role_set_is_denied_on_the_caused_path()
    {
        // The second drift direction: a command Role.System does not hold (CreateInventory is user-only)
        // is denied under SystemActor mode, so an erroneous caused dispatch of an out-of-set command
        // cannot slip through unenforced.
        var accessor = new StubCommandContextAccessor
        {
            Current = new StubCommandContext
            {
                AuthorizationMode = DispatchAuthorizationMode.SystemActor,
                Roles = new[] { Role.System },
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

    private static async Task AssertSystemActorAuthorizes<TCommand>(TCommand command)
        where TCommand : IAuthorizedCommand
    {
        var accessor = new StubCommandContextAccessor
        {
            Current = new StubCommandContext
            {
                AuthorizationMode = DispatchAuthorizationMode.SystemActor,
                Roles = new[] { Role.System },
            },
        };
        var nextInvoked = false;

        await new AuthorizationCommandBehavior<TCommand>(
                accessor, new PermissionAuthorizer(new RolePermissionRegistry(RolePermissionPolicy.Default)))
            .HandleAsync(command, () => { nextInvoked = true; return Task.CompletedTask; }, CancellationToken.None);

        nextInvoked.Should().BeTrue($"the System role authorizes {typeof(TCommand).Name} on the caused path");
    }
}
