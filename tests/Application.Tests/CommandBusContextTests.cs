using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class CommandBusContextTests
{
    [Fact]
    public async Task SendAsync_pushes_a_fresh_context_onto_the_accessor_for_the_handler()
    {
        var accessor = new AsyncLocalCommandContextAccessor();
        var capture = new ContextCapturingHandler(accessor);
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(capture)
            .AddSingleton<ICommandContextAccessor>(accessor)
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), CancellationToken.None);

        capture.Observed.Should().NotBeNull();
        capture.Observed!.CorrelationId.Should().NotBe(Guid.Empty);
        capture.Observed.CausationCommandId.Should().NotBe(Guid.Empty);
        capture.Observed.UserId.Should().Be("system");
        capture.Observed.ServiceName.Should().Be("Workers");
    }

    [Fact]
    public async Task SendAsync_restores_the_previous_accessor_value_after_the_handler_completes()
    {
        var accessor = new AsyncLocalCommandContextAccessor();
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(new ContextCapturingHandler(accessor))
            .AddSingleton<ICommandContextAccessor>(accessor)
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), CancellationToken.None);

        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_restores_the_previous_accessor_value_after_the_handler_throws()
    {
        var accessor = new AsyncLocalCommandContextAccessor();
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(new ThrowingHandler())
            .AddSingleton<ICommandContextAccessor>(accessor)
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        var act = () => bus.SendAsync(new DoThing(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_with_explicit_correlation_id_uses_the_provided_value()
    {
        var expected = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var accessor = new AsyncLocalCommandContextAccessor();
        var capture = new ContextCapturingHandler(accessor);
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(capture)
            .AddSingleton<ICommandContextAccessor>(accessor)
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), expected, CancellationToken.None);

        capture.Observed!.CorrelationId.Should().Be(expected);
    }

    private sealed record DoThing : ICommand;

    private sealed class ContextCapturingHandler : ICommandHandler<DoThing>
    {
        private readonly ICommandContextAccessor _accessor;

        public ContextCapturingHandler(ICommandContextAccessor accessor) => _accessor = accessor;

        public ICommandContext? Observed { get; private set; }

        public Task HandleAsync(DoThing command, CancellationToken ct)
        {
            Observed = _accessor.Current;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : ICommandHandler<DoThing>
    {
        public Task HandleAsync(DoThing command, CancellationToken ct)
            => throw new InvalidOperationException("handler failed");
    }
}
