using System.Collections.Concurrent;
using System.Reflection;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Application;

// Symmetric to CommandBus. The cache key is the concrete query type alone: a
// type can only implement IQuery<TResult> once (closing the same generic with
// two different TResults on one class is a compile error), so the cached
// invoker safely covers every AskAsync call against that query type.
public sealed class QueryBus : IQueryBus
{
    private static readonly ConcurrentDictionary<Type, QueryInvoker> InvokerCache = new();
    private readonly IServiceProvider _services;

    public QueryBus(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public Task<TResult> AskAsync<TResult>(IQuery<TResult> query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var invoker = InvokerCache.GetOrAdd(query.GetType(), t => BuildInvoker(t, typeof(TResult)));
        var handler = _services.GetRequiredService(invoker.HandlerType);
        var behaviors = _services.GetServices(invoker.BehaviorType).ToArray();
        var pipeline = QueryPipelineBuilder.Build<TResult>(
            behaviors, handler, query, invoker.HandleMethod, invoker.BehaviorHandleMethod, ct);
        return pipeline();
    }

    private static QueryInvoker BuildInvoker(Type queryType, Type resultType)
    {
        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(queryType, resultType);
        var behaviorType = typeof(IQueryPipelineBehavior<,>).MakeGenericType(queryType, resultType);
        return new QueryInvoker(
            HandlerType: handlerType,
            HandleMethod: handlerType.GetMethod("HandleAsync")!,
            BehaviorType: behaviorType,
            BehaviorHandleMethod: behaviorType.GetMethod("HandleAsync")!);
    }

    private sealed record QueryInvoker(
        Type HandlerType,
        MethodInfo HandleMethod,
        Type BehaviorType,
        MethodInfo BehaviorHandleMethod);
}
