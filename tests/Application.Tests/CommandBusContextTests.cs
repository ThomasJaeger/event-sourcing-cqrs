using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Application.Pipelines;
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
        capture.Observed.ActorId.Should().Be(Guid.Empty);
        capture.Observed.ServiceName.Should().Be("Workers");
        capture.Observed.IdempotencyKey.Should().BeNull();
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

    [Fact]
    public async Task SendAsync_with_an_idempotency_key_threads_it_onto_the_context_for_the_handler()
    {
        const string key = "11111111-1111-1111-1111-111111111111";
        var accessor = new AsyncLocalCommandContextAccessor();
        var capture = new ContextCapturingHandler(accessor);
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(capture)
            .AddSingleton<ICommandContextAccessor>(accessor)
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), key, CancellationToken.None);

        capture.Observed!.IdempotencyKey.Should().Be(key);
    }

    [Fact]
    public async Task SendAsync_with_a_null_idempotency_key_mints_the_same_context_as_the_bare_overload()
    {
        var accessor = new AsyncLocalCommandContextAccessor();
        var capture = new ContextCapturingHandler(accessor);
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(capture)
            .AddSingleton<ICommandContextAccessor>(accessor)
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), idempotencyKey: null, CancellationToken.None);

        capture.Observed!.IdempotencyKey.Should().BeNull();
        capture.Observed.CorrelationId.Should().NotBe(Guid.Empty);
        capture.Observed.CausationCommandId.Should().NotBe(Guid.Empty);
        capture.Observed.ActorId.Should().Be(Guid.Empty);
        capture.Observed.ServiceName.Should().Be("Workers");
    }

    [Fact]
    public async Task SendAsync_with_the_same_idempotency_key_twice_dispatches_the_handler_once()
    {
        // End-to-end: the key the bus threads onto the context is the key the real
        // IdempotencyBehavior reads from the accessor at runtime, so the second
        // dispatch dedupes. This is the wiring neither CommandBusContextTests (no
        // behavior) nor IdempotencyBehaviorTests (no bus) covers on its own.
        const string key = "22222222-2222-2222-2222-222222222222";
        var accessor = new AsyncLocalCommandContextAccessor();
        var capture = new ContextCapturingHandler(accessor);
        var store = new RecordingIdempotencyStore();
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(capture)
            .AddSingleton<ICommandContextAccessor>(accessor)
            .AddSingleton<IIdempotencyStore>(store)
            .AddSingleton(typeof(ICommandPipelineBehavior<>), typeof(IdempotencyBehavior<>))
            .BuildServiceProvider();
        var bus = new CommandBus(services);

        await bus.SendAsync(new DoThing(), key, CancellationToken.None);
        await bus.SendAsync(new DoThing(), key, CancellationToken.None);

        store.Recorded.Should().ContainSingle().Which.Should().Be(key);
        capture.Invocations.Should().Be(1);
    }

    private sealed record DoThing : ICommand;

    private sealed class ContextCapturingHandler : ICommandHandler<DoThing>
    {
        private readonly ICommandContextAccessor _accessor;

        public ContextCapturingHandler(ICommandContextAccessor accessor) => _accessor = accessor;

        public ICommandContext? Observed { get; private set; }
        public int Invocations { get; private set; }

        public Task HandleAsync(DoThing command, CancellationToken ct)
        {
            Invocations++;
            Observed = _accessor.Current;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : ICommandHandler<DoThing>
    {
        public Task HandleAsync(DoThing command, CancellationToken ct)
            => throw new InvalidOperationException("handler failed");
    }

    private sealed class RecordingIdempotencyStore : IIdempotencyStore
    {
        public List<string> Recorded { get; } = new();

        public Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct)
            => Task.FromResult(Recorded.Contains(idempotencyKey));

        public Task<bool> TryRecordAsync(string idempotencyKey, string commandType, CancellationToken ct)
        {
            Recorded.Add(idempotencyKey);
            return Task.FromResult(true);
        }
    }
}
