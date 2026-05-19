using System.Collections.Concurrent;
using System.Reflection;
using EventSourcingCqrs.Application.Context;
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
//
// Each dispatch opens a service scope, builds a fresh CommandContext, pushes
// it onto the AsyncLocal accessor, runs the pipeline, and restores the prior
// accessor value in finally so nested dispatches and exception paths leak no
// context.
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
        => SendInternal(command, Guid.NewGuid(), ct);

    // Overload for callers that already have a correlation ID (HTTP middleware
    // forwarding an X-Correlation-Id header, a process manager continuing an
    // existing causation chain). Lives on the concrete class so the interface
    // stays at the shape Ch 10 depicts; callers that need this take CommandBus
    // directly or push a pre-built context onto the accessor before calling
    // the interface form.
    public Task SendAsync(ICommand command, Guid correlationId, CancellationToken ct)
        => SendInternal(command, correlationId, ct);

    private async Task SendInternal(ICommand command, Guid correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var invoker = InvokerCache.GetOrAdd(command.GetType(), BuildInvoker);
        var handler = sp.GetRequiredService(invoker.HandlerType);
        var behaviors = sp.GetServices(invoker.BehaviorType).ToArray();
        var accessor = sp.GetRequiredService<ICommandContextAccessor>();
        var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
        var options = sp.GetService<ApplicationOptions>() ?? new ApplicationOptions();

        // ActorId stays Guid.Empty until Phase 7's HTTP middleware maps an
        // authenticated principal. ServiceName reads from ApplicationOptions,
        // defaulting to "Workers" when the host has not configured it.
        var context = new CommandContext(timeProvider)
        {
            CorrelationId = correlationId,
            CausationCommandId = Guid.NewGuid(),
            ActorId = Guid.Empty,
            ServiceName = options.ServiceName
        };

        var previous = accessor.Current;
        accessor.Current = context;
        try
        {
            var pipeline = CommandPipelineBuilder.Build(
                behaviors, handler, command, invoker.HandleMethod, invoker.BehaviorHandleMethod, ct);
            await pipeline();
        }
        finally
        {
            accessor.Current = previous;
        }
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
