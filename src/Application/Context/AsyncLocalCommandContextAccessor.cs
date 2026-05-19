using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Context;

// Singleton accessor backed by an AsyncLocal so a logical async flow carries
// its CommandContext through every handler, behavior, and serializer call,
// while concurrent flows stay isolated. Same pattern as IHttpContextAccessor.
// The bus sets Current before the pipeline runs and restores the previous
// value in a finally block so a nested dispatch sees its parent's context
// after the child returns.
public sealed class AsyncLocalCommandContextAccessor : ICommandContextAccessor
{
    private static readonly AsyncLocal<ICommandContext?> Holder = new();

    public ICommandContext? Current
    {
        get => Holder.Value;
        set => Holder.Value = value;
    }
}
