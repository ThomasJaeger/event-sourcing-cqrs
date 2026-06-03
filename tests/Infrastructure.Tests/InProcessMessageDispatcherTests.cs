// Unit tests for the reflection in InProcessMessageDispatcher: it builds a
// closed EventContext<TEvent> from an OutboxMessage and invokes every
// IEventHandler<TEvent> resolved from DI. The test event and handler are
// public so the dispatcher, which lives in another assembly, can reflect
// over them without an accessibility barrier.

using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Outbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests;

public class InProcessMessageDispatcherTests
{
    private static readonly DateTime At = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DispatchAsync_builds_EventContext_from_the_outbox_message()
    {
        var handler = new RecordingHandler("only", []);
        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<TestDispatchEvent>>(handler);
        services.AddSingleton<ICurrentTenantAccessor>(new TestKit.StubTenantAccessor());
        var dispatcher = new InProcessMessageDispatcher(services.BuildServiceProvider());

        var payload = new TestDispatchEvent("ship it");
        var metadata = BuildMetadata();
        var message = new OutboxMessage(
            OutboxId: 7,
            EventId: metadata.EventId,
            EventType: nameof(TestDispatchEvent),
            Event: payload,
            Metadata: metadata,
            GlobalPosition: 314,
            AttemptCount: 0);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        handler.LastContext.Should().NotBeNull();
        handler.LastContext!.Event.Should().Be(payload);
        handler.LastContext.Metadata.Should().Be(metadata);
        handler.LastContext.GlobalPosition.Should().Be(314);
    }

    [Fact]
    public async Task DispatchAsync_invokes_every_registered_handler_in_registration_order()
    {
        var invocationLog = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<TestDispatchEvent>>(
            new RecordingHandler("first", invocationLog));
        services.AddSingleton<IEventHandler<TestDispatchEvent>>(
            new RecordingHandler("second", invocationLog));
        services.AddSingleton<ICurrentTenantAccessor>(new TestKit.StubTenantAccessor());
        var dispatcher = new InProcessMessageDispatcher(services.BuildServiceProvider());

        var metadata = BuildMetadata();
        var message = new OutboxMessage(
            OutboxId: 1,
            EventId: metadata.EventId,
            EventType: nameof(TestDispatchEvent),
            Event: new TestDispatchEvent("go"),
            Metadata: metadata,
            GlobalPosition: 1,
            AttemptCount: 0);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        invocationLog.Should().Equal("first", "second");
    }

    [Fact]
    public async Task DispatchAsync_invokes_process_manager_handlers()
    {
        var handler = new RecordingPmHandler("pm", []);
        var services = new ServiceCollection();
        services.AddSingleton<IProcessManagerHandler<TestDispatchEvent>>(handler);
        services.AddSingleton<ICurrentTenantAccessor>(new TestKit.StubTenantAccessor());
        var dispatcher = new InProcessMessageDispatcher(services.BuildServiceProvider());

        var payload = new TestDispatchEvent("react");
        await dispatcher.DispatchAsync(Message(payload), CancellationToken.None);

        handler.LastContext.Should().NotBeNull();
        handler.LastContext!.Event.Should().Be(payload);
    }

    [Fact]
    public async Task DispatchAsync_invokes_event_handlers_before_process_manager_handlers()
    {
        var invocationLog = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<TestDispatchEvent>>(
            new RecordingHandler("projection", invocationLog));
        services.AddSingleton<IProcessManagerHandler<TestDispatchEvent>>(
            new RecordingPmHandler("process-manager", invocationLog));
        services.AddSingleton<ICurrentTenantAccessor>(new TestKit.StubTenantAccessor());
        var dispatcher = new InProcessMessageDispatcher(services.BuildServiceProvider());

        await dispatcher.DispatchAsync(Message(new TestDispatchEvent("go")), CancellationToken.None);

        // Projections run before process managers, so a read model a PM reads on
        // this event is updated first.
        invocationLog.Should().Equal("projection", "process-manager");
    }

    [Fact]
    public async Task DispatchAsync_resolves_a_fresh_scoped_process_manager_handler_per_message()
    {
        var instances = new List<object>();
        var services = new ServiceCollection();
        services.AddScoped<IProcessManagerHandler<TestDispatchEvent>>(
            _ => new InstanceRecordingPmHandler(instances));
        services.AddSingleton<ICurrentTenantAccessor>(new TestKit.StubTenantAccessor());
        // validateScopes: true would throw if the dispatcher resolved the scoped
        // handler from the root provider; it passes because the dispatcher opens a
        // scope per message, and the two dispatches yield two distinct instances.
        var dispatcher = new InProcessMessageDispatcher(
            services.BuildServiceProvider(validateScopes: true));

        await dispatcher.DispatchAsync(Message(new TestDispatchEvent("one")), CancellationToken.None);
        await dispatcher.DispatchAsync(Message(new TestDispatchEvent("two")), CancellationToken.None);

        instances.Should().HaveCount(2);
        instances[0].Should().NotBeSameAs(instances[1]);
    }

    private static OutboxMessage Message(TestDispatchEvent payload)
    {
        var metadata = BuildMetadata();
        return new OutboxMessage(
            OutboxId: 1,
            EventId: metadata.EventId,
            EventType: nameof(TestDispatchEvent),
            Event: payload,
            Metadata: metadata,
            GlobalPosition: 1,
            AttemptCount: 0);
    }

    private static EventMetadata BuildMetadata()
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: At,
            Tenant: WellKnownTenants.Default);

    public sealed record TestDispatchEvent(string Note) : IDomainEvent;

    public sealed class RecordingHandler : IEventHandler<TestDispatchEvent>
    {
        private readonly string _name;
        private readonly List<string> _invocationLog;

        public RecordingHandler(string name, List<string> invocationLog)
        {
            _name = name;
            _invocationLog = invocationLog;
        }

        public EventContext<TestDispatchEvent>? LastContext { get; private set; }

        public Task HandleAsync(EventContext<TestDispatchEvent> context, CancellationToken ct)
        {
            LastContext = context;
            _invocationLog.Add(_name);
            return Task.CompletedTask;
        }
    }

    public sealed class RecordingPmHandler : IProcessManagerHandler<TestDispatchEvent>
    {
        private readonly string _name;
        private readonly List<string> _invocationLog;

        public RecordingPmHandler(string name, List<string> invocationLog)
        {
            _name = name;
            _invocationLog = invocationLog;
        }

        public EventContext<TestDispatchEvent>? LastContext { get; private set; }

        public Task HandleAsync(EventContext<TestDispatchEvent> context, CancellationToken ct)
        {
            LastContext = context;
            _invocationLog.Add(_name);
            return Task.CompletedTask;
        }
    }

    public sealed class InstanceRecordingPmHandler : IProcessManagerHandler<TestDispatchEvent>
    {
        private readonly List<object> _instances;

        public InstanceRecordingPmHandler(List<object> instances) => _instances = instances;

        public Task HandleAsync(EventContext<TestDispatchEvent> context, CancellationToken ct)
        {
            _instances.Add(this);
            return Task.CompletedTask;
        }
    }
}
