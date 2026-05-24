using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class CommandBusTests
{
    [Fact]
    public async Task SendAsync_dispatches_to_registered_handler()
    {
        var handler = new RecordingHandler();
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(handler)
            .AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>()
            .BuildServiceProvider();
        var bus = new CommandBus(services);
        var command = new DoThing("hello");

        await bus.SendAsync(command, CancellationToken.None);

        handler.Received.Should().BeSameAs(command);
    }

    [Fact]
    public async Task SendAsync_throws_when_handler_not_registered()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>()
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        var act = () => bus.SendAsync(new DoThing("x"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SendAsync_passes_cancellation_token_to_handler()
    {
        var handler = new RecordingHandler();
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(handler)
            .AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>()
            .BuildServiceProvider();
        var bus = new CommandBus(services);
        using var cts = new CancellationTokenSource();

        await bus.SendAsync(new DoThing("x"), cts.Token);

        handler.ReceivedToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task SendAsync_resolves_a_scoped_handler_under_scope_validation()
    {
        // ADR 0024: scope-per-dispatch holds on the command bus too. Locks the
        // already-established pattern against regression under a validating provider.
        var services = new ServiceCollection()
            .AddScoped<ICommandHandler<DoThing>, RecordingHandler>()
            .AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var bus = new CommandBus(services);

        var act = async () => await bus.SendAsync(new DoThing("x"), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private sealed record DoThing(string Payload) : ICommand;

    private sealed class RecordingHandler : ICommandHandler<DoThing>
    {
        public DoThing? Received { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }

        public Task HandleAsync(DoThing command, CancellationToken ct)
        {
            Received = command;
            ReceivedToken = ct;
            return Task.CompletedTask;
        }
    }
}
