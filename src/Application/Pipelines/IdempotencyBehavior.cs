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

    public IdempotencyBehavior(IIdempotencyStore store, ICommandContextAccessor accessor)
    {
        _store = store;
        _accessor = accessor;
    }

    public async Task HandleAsync(TCommand command, CommandHandlerDelegate next, CancellationToken ct)
    {
        var key = _accessor.Current?.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            await next();
            return;
        }

        if (await _store.ExistsAsync(key, ct))
        {
            return;
        }

        await next();

        // Recorded only after the handler succeeds, so a failed command stays
        // retryable. The table is a best-effort short-circuit, not the
        // correctness layer: on a false return or a record that never lands,
        // the event store's per-stream version constraint is the backstop
        // against a double-apply on retry (ADR 0016).
        await _store.TryRecordAsync(key, typeof(TCommand).Name, ct);
    }
}
