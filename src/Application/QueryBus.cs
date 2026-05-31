using System.Collections.Concurrent;
using System.Reflection;
using EventSourcingCqrs.Application.Context;
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
//
// Both overloads run one shared dispatch path that opens the scope, resolves the
// query-context accessor, pushes the context for the pipeline's duration, and
// restores the prior value in finally, exactly as CommandBus.DispatchAsync does.
// The bare overload builds a non-authenticated context (Phase 9); the principal
// overload builds an authenticated one carrying the actor and roles, which the
// authorization behavior and the ownership-filtering handlers read off the accessor.
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
        return DispatchAsync(
            query,
            new QueryContext { ActorId = Guid.Empty },
            ct);
    }

    public Task<TResult> AskAsync<TResult>(
        IQuery<TResult> query, Guid actorId, IReadOnlyCollection<Role> roles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(roles);
        return DispatchAsync(
            query,
            new QueryContext
            {
                ActorId = actorId,
                Roles = roles,
                IsAuthenticatedUserQuery = true
            },
            ct);
    }

    // The one dispatch loop both overloads run: open a scope, resolve the handler,
    // behaviors, and accessor, push the caller's context, await the pipeline inside
    // the using so the scope outlives the dispatch (ADR 0024), and restore the prior
    // accessor value in finally so nested dispatches and exception paths leak no
    // context. Returning the pipeline directly would dispose the scope before the
    // handler ran.
    private async Task<TResult> DispatchAsync<TResult>(
        IQuery<TResult> query, IQueryContext context, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var invoker = InvokerCache.GetOrAdd(query.GetType(), t => BuildInvoker(t, typeof(TResult)));
        var handler = sp.GetRequiredService(invoker.HandlerType);
        var behaviors = sp.GetServices(invoker.BehaviorType).ToArray();
        var accessor = sp.GetRequiredService<IQueryContextAccessor>();

        var previous = accessor.Current;
        accessor.Current = context;
        try
        {
            var pipeline = QueryPipelineBuilder.Build<TResult>(
                behaviors, handler, query, invoker.HandleMethod, invoker.BehaviorHandleMethod, ct);
            return await pipeline();
        }
        finally
        {
            accessor.Current = previous;
        }
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
