using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authorization;

public class CommandPermissionValidationTests
{
    // A concrete command that declares no permission. It lives in the test assembly, so the real
    // composition walk never sees it; it proves FindUndeclared catches the gap without planting a
    // non-conforming command in the Application assembly the structural check scans.
    private sealed record UndeclaredCommand : ICommand;

    [Fact]
    public void Every_concrete_command_in_the_application_assembly_declares_a_permission()
    {
        var undeclared = CommandPermissionValidation.FindUndeclared(
            typeof(PlaceOrder).Assembly.GetTypes());

        undeclared.Should().BeEmpty();
    }

    [Fact]
    public void FindUndeclared_flags_a_command_that_declares_no_permission()
    {
        var undeclared = CommandPermissionValidation.FindUndeclared(new[] { typeof(UndeclaredCommand) });

        undeclared.Should().ContainSingle().Which.Should().Be(typeof(UndeclaredCommand));
    }

    [Fact]
    public void FindUndeclared_ignores_a_command_that_declares_a_permission()
    {
        var undeclared = CommandPermissionValidation.FindUndeclared(new[] { typeof(PlaceOrder) });

        undeclared.Should().BeEmpty();
    }
}
