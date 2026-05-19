using System.Collections.Concurrent;
using System.Reflection;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Application;

// Hand-rolled command dispatch. Same shape as InProcessMessageDispatcher: the
// runtime command type closes the generic ICommandHandler<TCommand>, the closed
// type plus its HandleAsync method get cached once per command type, and every
// subsequent dispatch is one DI lookup plus one MethodInfo.Invoke. A compiled
// delegate cache would skip the Invoke cost but adds machinery; v1 keeps the
// shape simple and a later commit can swap it in if a profiler asks for it.
public sealed class CommandBus : ICommandBus
{
    private static readonly ConcurrentDictionary<Type, CommandInvoker> InvokerCache = new();
    private readonly IServiceProvider _services;

    public CommandBus(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public Task SendAsync(ICommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var invoker = InvokerCache.GetOrAdd(command.GetType(), BuildInvoker);
        var handler = _services.GetRequiredService(invoker.HandlerType);
        var behaviors = _services.GetServices(invoker.BehaviorType).ToArray();
        var pipeline = CommandPipelineBuilder.Build(
            behaviors, handler, command, invoker.HandleMethod, invoker.BehaviorHandleMethod, ct);
        return pipeline();
    }

    private static CommandInvoker BuildInvoker(Type commandType)
    {
        var handlerType = typeof(ICommandHandler<>).MakeGenericType(commandType);
        var behaviorType = typeof(ICommandPipelineBehavior<>).MakeGenericType(commandType);
        return new CommandInvoker(
            HandlerType: handlerType,
            HandleMethod: handlerType.GetMethod(nameof(ICommandHandler<ICommand>.HandleAsync))!,
            BehaviorType: behaviorType,
            BehaviorHandleMethod: behaviorType.GetMethod(nameof(ICommandPipelineBehavior<ICommand>.HandleAsync))!);
    }

    private sealed record CommandInvoker(
        Type HandlerType,
        MethodInfo HandleMethod,
        Type BehaviorType,
        MethodInfo BehaviorHandleMethod);
}
