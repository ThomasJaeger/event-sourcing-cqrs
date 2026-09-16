using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Pipelines;

// Deduplicates commands that carry an idempotency key (ADR 0016). Reads the key
// off ICommandContextAccessor the same way LoggingCommandBehavior reads the
// correlation id. A null or blank key means dedup was not requested (the
// user-dispatch path), so the behavior is a passthrough. With a key, ExistsAsync
// is the eager check: a hit short-circuits as a no-op success (void commands
// have nothing to return). On a miss the handler runs, and only on success is
// the key recorded, so a failed command stays retryable. Sits inside
// LoggingCommandBehavior so a duplicate is still logged, and before
// ValidationCommandBehavior so a duplicate does no validation work.
public sealed class IdempotencyBehavior<TCommand> : ICommandPipelineBehavior<TCommand>
    where TCommand : ICommand
{
    private readonly IIdempotencyStore _store;
    private readonly ICommandContextAccessor _accessor;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public IdempotencyBehavior(
        IIdempotencyStore store, ICommandContextAccessor accessor, ICurrentTenantAccessor tenantAccessor)
    {
        _store = store;
        _accessor = accessor;
        _tenantAccessor = tenantAccessor;
    }

    public async Task HandleAsync(TCommand command, CommandHandlerDelegate next, CancellationToken ct)
    {
        var key = _accessor.Current?.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            await next();
            return;
        }

        // The narrowest shipped store uses a 200-byte UTF-8 key. Reject before
        // executing any effects, rather than discovering an oversized key afterward.
        if (System.Text.Encoding.UTF8.GetByteCount(key) > 200)
            throw new ValidationException([new ValidationError("IdempotencyKey", "Must not exceed 200 UTF-8 bytes.")]);

        var tenant = _tenantAccessor.Current ?? throw new MissingTenantContextException();

        if (await _store.ExistsAsync(tenant, key, ct))
        {
            return;
        }

        try
        {
            await next();
        }
        catch (CommandAlreadyCommittedException)
        {
            // The event stream is the receipt; rebuild the optional cache below.
        }

        // This is an optimization. Repositories enforce the key against metadata
        // committed atomically with the effects, including after a crash here.
        await _store.TryRecordAsync(tenant, key, typeof(TCommand).Name, ct);
    }
}
