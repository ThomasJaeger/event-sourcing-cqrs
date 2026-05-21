using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class CausedCommandBusTests
{
    private static readonly SystemActor Actor = SystemActors.OrderFulfillment;

    private static EventMetadata CausingEvent(Guid correlationId, Guid eventId)
        => new(
            EventId: eventId,
            CorrelationId: correlationId,
            CausationId: Guid.NewGuid(),
            ActorId: Guid.NewGuid(),
            Source: "Sales",
            SchemaVersion: 1,
            OccurredUtc: new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task SendAsync_threads_correlation_and_causation_from_the_causing_event()
    {
        var correlationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var causingEventId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var accessor = new AsyncLocalCommandContextAccessor();
        var capture = new ContextCapturingHandler(accessor);
        var caused = BuildBus(accessor, capture);

        await caused.SendAsync(
            new DoThing(), CausingEvent(correlationId, causingEventId),
            Actor, idempotencyKey: null, CancellationToken.None);

        capture.Observed.Should().NotBeNull();
        capture.Observed!.CorrelationId.Should().Be(correlationId);
        // Event-to-event causation: the command's causation source is the
        // causing event's EventId, not a freshly-minted command id (ADR 0014).
        capture.Observed.CausationCommandId.Should().Be(causingEventId);
    }

    [Fact]
    public async Task SendAsync_stamps_actor_id_and_service_name_from_the_system_actor()
    {
        var accessor = new AsyncLocalCommandContextAccessor();
        var capture = new ContextCapturingHandler(accessor);
        var caused = BuildBus(accessor, capture);

        await caused.SendAsync(
            new DoThing(), CausingEvent(Guid.NewGuid(), Guid.NewGuid()),
            Actor, idempotencyKey: null, CancellationToken.None);

        capture.Observed!.ActorId.Should().Be(Actor.Id);
        capture.Observed.ServiceName.Should().Be(Actor.ServiceName);
    }

    [Fact]
    public async Task SendAsync_passes_the_idempotency_key_through_to_the_context()
    {
        const string key = "pm-order-fulfillment:7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e:authorize-payment";
        var accessor = new AsyncLocalCommandContextAccessor();
        var capture = new ContextCapturingHandler(accessor);
        var caused = BuildBus(accessor, capture);

        await caused.SendAsync(
            new DoThing(), CausingEvent(Guid.NewGuid(), Guid.NewGuid()),
            Actor, key, CancellationToken.None);

        capture.Observed!.IdempotencyKey.Should().Be(key);
    }

    [Fact]
    public async Task SendAsync_leaves_the_idempotency_key_null_when_none_is_supplied()
    {
        var accessor = new AsyncLocalCommandContextAccessor();
        var capture = new ContextCapturingHandler(accessor);
        var caused = BuildBus(accessor, capture);

        await caused.SendAsync(
            new DoThing(), CausingEvent(Guid.NewGuid(), Guid.NewGuid()),
            Actor, idempotencyKey: null, CancellationToken.None);

        capture.Observed!.IdempotencyKey.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_restores_the_previous_accessor_value_after_the_handler_completes()
    {
        // The PM-dispatch path runs the same shared loop as user dispatch, so
        // this mirrors CommandBusContextTests' user-path restoration check: a
        // leak on either path would surface in the other's test.
        var accessor = new AsyncLocalCommandContextAccessor();
        var caused = BuildBus(accessor, new ContextCapturingHandler(accessor));

        await caused.SendAsync(
            new DoThing(), CausingEvent(Guid.NewGuid(), Guid.NewGuid()),
            Actor, idempotencyKey: null, CancellationToken.None);

        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_nested_dispatch_restores_the_outer_context_when_the_inner_returns()
    {
        var accessor = new AsyncLocalCommandContextAccessor();
        var inner = new ContextCapturingHandler(accessor);
        var nesting = new NestingHandler(accessor)
        {
            InnerCausingEvent = CausingEvent(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                Guid.NewGuid())
        };
        var services = new ServiceCollection()
            .AddSingleton<ICommandHandler<DoThing>>(nesting)
            .AddSingleton<ICommandHandler<DoOther>>(inner)
            .AddSingleton<ICommandContextAccessor>(accessor)
            .BuildServiceProvider();
        var caused = new CausedCommandBus(new CommandBus(services));
        nesting.Inner = caused;
        var outerCorrelation = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        await caused.SendAsync(
            new DoThing(), CausingEvent(outerCorrelation, Guid.NewGuid()),
            Actor, idempotencyKey: null, CancellationToken.None);

        // The inner dispatch ran under its own context, and the outer context
        // was the same instance before and after the inner returned.
        inner.Observed!.CorrelationId.Should().Be("cccccccc-cccc-cccc-cccc-cccccccccccc");
        nesting.OuterBeforeInner.Should().NotBeNull();
        nesting.OuterBeforeInner!.CorrelationId.Should().Be(outerCorrelation);
        nesting.OuterAfterInner.Should().BeSameAs(nesting.OuterBeforeInner);
    }

    private static CausedCommandBus BuildBus(
        ICommandContextAccessor accessor, ICommandHandler<DoThing> handler)
    {
        var services = new ServiceCollection()
            .AddSingleton(handler)
            .AddSingleton<ICommandContextAccessor>(accessor)
            .BuildServiceProvider();
        return new CausedCommandBus(new CommandBus(services));
    }

    private sealed record DoThing : ICommand;

    private sealed record DoOther : ICommand;

    private sealed class ContextCapturingHandler
        : ICommandHandler<DoThing>, ICommandHandler<DoOther>
    {
        private readonly ICommandContextAccessor _accessor;

        public ContextCapturingHandler(ICommandContextAccessor accessor) => _accessor = accessor;

        public ICommandContext? Observed { get; private set; }

        public Task HandleAsync(DoThing command, CancellationToken ct)
        {
            Observed = _accessor.Current;
            return Task.CompletedTask;
        }

        public Task HandleAsync(DoOther command, CancellationToken ct)
        {
            Observed = _accessor.Current;
            return Task.CompletedTask;
        }
    }

    private sealed class NestingHandler : ICommandHandler<DoThing>
    {
        private readonly ICommandContextAccessor _accessor;

        public NestingHandler(ICommandContextAccessor accessor) => _accessor = accessor;

        public ICausedCommandBus? Inner { get; set; }
        public EventMetadata? InnerCausingEvent { get; set; }
        public ICommandContext? OuterBeforeInner { get; private set; }
        public ICommandContext? OuterAfterInner { get; private set; }

        public async Task HandleAsync(DoThing command, CancellationToken ct)
        {
            OuterBeforeInner = _accessor.Current;
            await Inner!.SendAsync(
                new DoOther(), InnerCausingEvent!, SystemActors.Return, idempotencyKey: null, ct);
            OuterAfterInner = _accessor.Current;
        }
    }
}
