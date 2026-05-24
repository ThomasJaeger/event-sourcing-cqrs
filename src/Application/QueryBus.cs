using System.Collections.Concurrent;
using System.Reflection;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Application;

// Symmetric to CommandBus, including scope-per-dispatch: each AskAsync opens a
// service scope and resolves the handler from it, because the handlers register
// scoped and this bus is a singleton holding the root provider, which cannot
// resolve a scoped service under scope validation (ADR 0024). The cache key is
// the concrete query type alone: a type can only implement IQuery<TResult> once
// (closing the same generic with two different TResults on one class is a compile
// error), so the cached invoker safely covers every AskAsync call against that
// query type.
public sealed class QueryBus : IQueryBus
{
    private static readonly ConcurrentDictionary<Type, QueryInvoker> InvokerCache = new();
    private readonly IServiceProvider _services;

    public QueryBus(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public async Task<TResult> AskAsync<TResult>(IQuery<TResult> query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Open a scope and resolve from it, awaiting the pipeline inside the using so
        // the scope outlives the dispatch (ADR 0024). Returning pipeline() directly
        // would dispose the scope before the handler ran.
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var invoker = InvokerCache.GetOrAdd(query.GetType(), t => BuildInvoker(t, typeof(TResult)));
        var handler = sp.GetRequiredService(invoker.HandlerType);
        var behaviors = sp.GetServices(invoker.BehaviorType).ToArray();
        var pipeline = QueryPipelineBuilder.Build<TResult>(
            behaviors, handler, query, invoker.HandleMethod, invoker.BehaviorHandleMethod, ct);
        return await pipeline();
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
