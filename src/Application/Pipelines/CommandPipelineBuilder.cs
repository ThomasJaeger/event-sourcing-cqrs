using System.Reflection;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Pipelines;

// Folds a registered behavior chain around the handler. The fold runs in
// reverse so behavior[0] becomes the outermost wrapper, matching DI's
// registration order. Each behavior receives a `next` delegate it may call
// (continue) or skip (short-circuit, which is how ValidationBehavior rejects
// before the handler runs).
//
// DoNotWrapExceptions lets a synchronously-thrown exception from the handler
// or a behavior propagate directly. Without it, MethodInfo.Invoke wraps in
// TargetInvocationException and callers have to unwrap to see the real cause.
internal static class CommandPipelineBuilder
{
    private const BindingFlags InvokeFlags = BindingFlags.DoNotWrapExceptions;

    public static CommandHandlerDelegate Build(
        object?[] behaviors,
        object handler,
        ICommand command,
        MethodInfo handlerMethod,
        MethodInfo behaviorMethod,
        CancellationToken ct)
    {
        CommandHandlerDelegate next = () =>
            (Task)handlerMethod.Invoke(handler, InvokeFlags, binder: null, new object[] { command, ct }, culture: null)!;

        for (int i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i]!;
            var current = next;
            next = () =>
                (Task)behaviorMethod.Invoke(behavior, InvokeFlags, binder: null, new object[] { command, current, ct }, culture: null)!;
        }

        return next;
    }
}
