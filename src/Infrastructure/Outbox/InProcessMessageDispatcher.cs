using System.Collections.Concurrent;
using System.Reflection;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Infrastructure.Outbox;

// Pattern from Chapter 8: outbox-to-bus dispatch. The in-process variant opens a
// DI scope per message and resolves, in container order, zero-to-many
// IEventHandler<TEvent> (projections, singletons) then zero-to-many
// IProcessManagerHandler<TEvent> (process managers, scoped). The scope is what
// lets the scoped PM handlers and their scoped repositories resolve, the same
// shape CommandBus uses per command; projections run before process managers so a
// read model a PM might read on this event is updated first. Engine-agnostic;
// per-adapter OutboxProcessor implementations dispatch through this one class. ADR
// 0004's per-adapter principle covers engine-specific outbox mechanics, not this
// consumer-side resolver.
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

        // A DI scope per message so scoped consumers resolve. A handler that throws
        // propagates so the OutboxProcessor retries the whole message; idempotent
        // projections and keyed PM dispatches make the retry safe.
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;

        // The projection write path reads the current tenant off this accessor (ADR
        // 0031). Set it from the event's metadata for the IEventHandler (projection)
        // loop and restore in finally before the process-manager loop, which stays
        // untenanted this commit. Same capture/set/restore-in-finally shape as CommandBus.
        var tenantAccessor = sp.GetRequiredService<ICurrentTenantAccessor>();
        var previousTenant = tenantAccessor.Current;
        tenantAccessor.Current = message.Metadata.Tenant;
        try
        {
            foreach (var handler in sp.GetServices(invoker.EventHandlerType))
            {
                await (Task)invoker.HandleMethod.Invoke(handler, new object[] { context, ct })!;
            }
        }
        finally
        {
            tenantAccessor.Current = previousTenant;
        }
        foreach (var handler in sp.GetServices(invoker.ProcessManagerHandlerType))
        {
            await (Task)invoker.ProcessManagerHandleMethod.Invoke(handler, new object[] { context, ct })!;
        }
    }

    private static HandlerInvoker BuildInvoker(Type eventType)
    {
        var eventHandlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
        var pmHandlerType = typeof(IProcessManagerHandler<>).MakeGenericType(eventType);
        var contextType = typeof(EventContext<>).MakeGenericType(eventType);
        return new HandlerInvoker(
            EventHandlerType: eventHandlerType,
            HandleMethod: eventHandlerType.GetMethod(nameof(IEventHandler<IDomainEvent>.HandleAsync))!,
            ProcessManagerHandlerType: pmHandlerType,
            ProcessManagerHandleMethod:
                pmHandlerType.GetMethod(nameof(IProcessManagerHandler<IDomainEvent>.HandleAsync))!,
            // A sealed record exposes exactly one public constructor, the
            // primary one: (TEvent Event, EventMetadata Metadata, long GlobalPosition).
            ContextConstructor: contextType.GetConstructors().Single());
    }

    private sealed record HandlerInvoker(
        Type EventHandlerType,
        MethodInfo HandleMethod,
        Type ProcessManagerHandlerType,
        MethodInfo ProcessManagerHandleMethod,
        ConstructorInfo ContextConstructor);
}
