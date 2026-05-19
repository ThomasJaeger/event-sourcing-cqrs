using System.Reflection;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Pipelines;

internal static class QueryPipelineBuilder
{
    private const BindingFlags InvokeFlags = BindingFlags.DoNotWrapExceptions;

    public static QueryHandlerDelegate<TResult> Build<TResult>(
        object?[] behaviors,
        object handler,
        IQuery<TResult> query,
        MethodInfo handlerMethod,
        MethodInfo behaviorMethod,
        CancellationToken ct)
    {
        QueryHandlerDelegate<TResult> next = () =>
            (Task<TResult>)handlerMethod.Invoke(handler, InvokeFlags, binder: null, new object[] { query, ct }, culture: null)!;

        for (int i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i]!;
            var current = next;
            next = () =>
                (Task<TResult>)behaviorMethod.Invoke(behavior, InvokeFlags, binder: null, new object[] { query, current, ct }, culture: null)!;
        }

        return next;
    }
}
