using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Context;

// Singleton accessor backed by an AsyncLocal so a logical async flow carries its QueryContext through
// every behavior and handler, while concurrent flows stay isolated. Same pattern as
// AsyncLocalCommandContextAccessor and IHttpContextAccessor. The bus sets Current before the pipeline
// runs and restores the previous value in a finally block so a nested dispatch sees its parent's
// context after the child returns.
public sealed class AsyncLocalQueryContextAccessor : IQueryContextAccessor
{
    private static readonly AsyncLocal<IQueryContext?> Holder = new();

    public IQueryContext? Current
    {
        get => Holder.Value;
        set => Holder.Value = value;
    }
}
