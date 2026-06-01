using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class ValidationBehaviorTests
{
    [Fact]
    public async Task Short_circuits_with_ValidationException_when_a_validator_returns_errors()
    {
        var handler = new TrackingHandler();
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(handler)
            .AddSingleton<IValidator<DoThing>>(new FailingValidator("field", "is required"))
            .AddSingleton<ICommandPipelineBehavior<DoThing>, ValidationCommandBehavior<DoThing>>()
            .AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>()
            .AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>()
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        var act = () => bus.SendAsync(new DoThing(), CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<ValidationException>()).Which;
        thrown.Errors.Should().ContainSingle()
            .Which.Should().Be(new ValidationError("field", "is required"));
        handler.Called.Should().BeFalse();
    }

    [Fact]
    public async Task Passes_through_to_handler_when_no_validators_are_registered()
    {
        var handler = new TrackingHandler();
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(handler)
            .AddSingleton<ICommandPipelineBehavior<DoThing>, ValidationCommandBehavior<DoThing>>()
            .AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>()
            .AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>()
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), CancellationToken.None);

        handler.Called.Should().BeTrue();
    }

    [Fact]
    public async Task Aggregates_errors_from_every_registered_validator_before_throwing()
    {
        var handler = new TrackingHandler();
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(handler)
            .AddSingleton<IValidator<DoThing>>(new FailingValidator("a", "bad"))
            .AddSingleton<IValidator<DoThing>>(new FailingValidator("b", "also bad"))
            .AddSingleton<ICommandPipelineBehavior<DoThing>, ValidationCommandBehavior<DoThing>>()
            .AddSingleton<ICommandContextAccessor, AsyncLocalCommandContextAccessor>()
            .AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>()
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        var act = () => bus.SendAsync(new DoThing(), CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<ValidationException>()).Which;
        thrown.Errors.Should().HaveCount(2);
        thrown.Errors.Should().Contain(new ValidationError("a", "bad"));
        thrown.Errors.Should().Contain(new ValidationError("b", "also bad"));
    }

    private sealed record DoThing : ICommand;

    private sealed class TrackingHandler : ICommandHandler<DoThing>
    {
        public bool Called { get; private set; }
        public Task HandleAsync(DoThing command, CancellationToken ct)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingValidator : IValidator<DoThing>
    {
        private readonly ValidationError _error;
        public FailingValidator(string path, string message) => _error = new ValidationError(path, message);
        public Task<IReadOnlyList<ValidationError>> ValidateAsync(DoThing command, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ValidationError>>(new[] { _error });
    }
}
