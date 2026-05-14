using System.Collections.Concurrent;
using System.Reflection;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Infrastructure.Outbox;

// Pattern from Chapter 8: outbox-to-bus dispatch. The in-process variant
// resolves zero-to-many IEventHandler<TEvent> registered in DI and invokes
// them in container order. Engine-agnostic; per-adapter OutboxProcessor
// implementations (PostgreSQL today, SQL Server next) both dispatch through
// this one class. ADR 0004's per-adapter principle covers engine-specific
// outbox mechanics, not this consumer-side resolver.
public sealed class InProcessMessageDispatcher : IMessageDispatcher
{
    // One invoker per event type: the closed IEventHandler<TEvent> to resolve,
    // its HandleAsync, and the EventContext<TEvent> constructor. Cached because
    // MakeGenericType plus the reflection lookups are not worth repeating per
    // message; the cache key is the runtime event type.
    private static readonly ConcurrentDictionary<Type, HandlerInvoker> InvokerCache = new();
    private readonly IServiceProvider _services;

    public InProcessMessageDispatcher(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public async Task DispatchAsync(OutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var invoker = InvokerCache.GetOrAdd(message.Event.GetType(), BuildInvoker);

        // EventContext<TEvent> is built once per message and shared across every
        // handler registered for that event type.
        var context = invoker.ContextConstructor.Invoke(
            new object[] { message.Event, message.Metadata, message.GlobalPosition });

        foreach (var handler in _services.GetServices(invoker.HandlerType))
        {
            await (Task)invoker.HandleMethod.Invoke(handler, new object[] { context, ct })!;
        }
    }

    private static HandlerInvoker BuildInvoker(Type eventType)
    {
        var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
        var contextType = typeof(EventContext<>).MakeGenericType(eventType);
        return new HandlerInvoker(
            HandlerType: handlerType,
            HandleMethod: handlerType.GetMethod(nameof(IEventHandler<IDomainEvent>.HandleAsync))!,
            // A sealed record exposes exactly one public constructor, the
            // primary one: (TEvent Event, EventMetadata Metadata, long GlobalPosition).
            ContextConstructor: contextType.GetConstructors().Single());
    }

    private sealed record HandlerInvoker(
        Type HandlerType,
        MethodInfo HandleMethod,
        ConstructorInfo ContextConstructor);
}
