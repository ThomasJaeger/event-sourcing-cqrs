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
    private static readonly ConcurrentDictionary<Type, MethodInfo> HandleMethodCache = new();
    private readonly IServiceProvider _services;

    public InProcessMessageDispatcher(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public async Task DispatchAsync(OutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var eventType = message.Event.GetType();
        var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
        var method = HandleMethodCache.GetOrAdd(
            eventType,
            _ => handlerType.GetMethod(nameof(IEventHandler<IDomainEvent>.HandleAsync))!);

        foreach (var handler in _services.GetServices(handlerType))
        {
            await (Task)method.Invoke(handler, new object[] { message.Event, ct })!;
        }
    }
}
