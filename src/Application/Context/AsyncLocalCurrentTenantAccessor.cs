using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Context;

// Singleton accessor backed by an AsyncLocal so a logical async flow carries its
// tenant through every handler, behavior, and serializer call, while concurrent
// flows stay isolated. Same pattern as AsyncLocalCommandContextAccessor and
// AsyncLocalQueryContextAccessor. The command bus sets Current before the pipeline
// runs and restores the previous value in a finally block so a nested dispatch
// sees its parent's tenant after the child returns.
public sealed class AsyncLocalCurrentTenantAccessor : ICurrentTenantAccessor
{
    private static readonly AsyncLocal<TenantId?> Holder = new();

    public TenantId? Current
    {
        get => Holder.Value;
        set => Holder.Value = value;
    }
}
