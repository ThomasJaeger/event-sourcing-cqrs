using System.Reflection;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Pipelines;

// Folds a registered behavior chain around the handler. The fold runs in
// reverse so behavior[0] becomes the outermost wrapper, matching DI's
// registration order. Each behavior receives a `next` delegate it may call
// (continue) or skip (short-circuit, which is how ValidationBehavior rejects
// before the handler runs).
internal static class CommandPipelineBuilder
{
    public static CommandHandlerDelegate Build(
        object?[] behaviors,
        object handler,
        ICommand command,
        MethodInfo handlerMethod,
        MethodInfo behaviorMethod,
        CancellationToken ct)
    {
        CommandHandlerDelegate next = () =>
            (Task)handlerMethod.Invoke(handler, new object[] { command, ct })!;

        for (int i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i]!;
            var current = next;
            next = () =>
                (Task)behaviorMethod.Invoke(behavior, new object[] { command, current, ct })!;
        }

        return next;
    }
}
