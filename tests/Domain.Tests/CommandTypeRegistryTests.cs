using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests;

public class CommandTypeRegistryTests
{
    [Fact]
    public void Register_succeeds_when_type_is_new()
    {
        var registry = new CommandTypeRegistry()
            .Register<CommandA>();

        registry.NameFor(typeof(CommandA)).Should().Be(nameof(CommandA));
        registry.TypeFor(nameof(CommandA)).Should().Be(typeof(CommandA));
    }

    [Fact]
    public void Register_returns_self_for_fluent_chaining()
    {
        var registry = new CommandTypeRegistry()
            .Register<CommandA>()
            .Register<CommandB>();

        registry.NameFor(typeof(CommandA)).Should().Be(nameof(CommandA));
        registry.NameFor(typeof(CommandB)).Should().Be(nameof(CommandB));
    }

    [Fact]
    public void Register_with_explicit_name_uses_that_name()
    {
        var registry = new CommandTypeRegistry()
            .Register<CommandA>("command_a_v1");

        registry.NameFor(typeof(CommandA)).Should().Be("command_a_v1");
        registry.TypeFor("command_a_v1").Should().Be(typeof(CommandA));
    }

    [Fact]
    public void Register_throws_when_same_clr_type_registered_twice()
    {
        var registry = new CommandTypeRegistry()
            .Register<CommandA>();

        var act = () => registry.Register<CommandA>("alias");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CommandA*already registered*alias*");
    }

    [Fact]
    public void Register_throws_when_same_storage_name_registered_twice()
    {
        var registry = new CommandTypeRegistry()
            .Register<CommandA>("collision");

        var act = () => registry.Register<CommandB>("collision");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'collision'*already registered*CommandA*CommandB*");
    }

    [Fact]
    public void NameFor_throws_UnknownCommandTypeException_when_type_not_registered()
    {
        var registry = new CommandTypeRegistry();

        var act = () => registry.NameFor(typeof(CommandA));

        act.Should().Throw<UnknownCommandTypeException>()
            .Which.TypeName.Should().Contain(nameof(CommandA));
    }

    [Fact]
    public void TypeFor_throws_UnknownCommandTypeException_when_name_not_registered()
    {
        var registry = new CommandTypeRegistry();

        var act = () => registry.TypeFor("not_a_real_command");

        act.Should().Throw<UnknownCommandTypeException>()
            .Which.TypeName.Should().Be("not_a_real_command");
    }

    [Fact]
    public void TypeFor_is_case_sensitive()
    {
        var registry = new CommandTypeRegistry()
            .Register<CommandA>("CommandA");

        var act = () => registry.TypeFor("commanda");

        act.Should().Throw<UnknownCommandTypeException>();
    }

    [Fact]
    public void Register_throws_when_Type_does_not_implement_ICommand()
    {
        var registry = new CommandTypeRegistry();

        var act = () => registry.Register(typeof(NotACommand));

        // The generic Register<TCommand> form rejects this at compile time
        // through the ICommand constraint; the non-generic Register(Type) path
        // used by ICommandTypeProvider enforces the same rule at runtime.
        act.Should().Throw<ArgumentException>()
            .WithMessage("*NotACommand*does not implement ICommand*");
    }

    [Fact]
    public void EnumerateCommands_returns_all_registered_types()
    {
        var registry = new CommandTypeRegistry()
            .Register<CommandA>()
            .Register<CommandB>();

        registry.EnumerateCommands().Should().BeEquivalentTo(
            new[] { typeof(CommandA), typeof(CommandB) });
    }

    private sealed record CommandA : ICommand;
    private sealed record CommandB : ICommand;
    private sealed class NotACommand;
}
