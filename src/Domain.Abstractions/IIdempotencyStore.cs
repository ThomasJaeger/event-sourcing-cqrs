namespace EventSourcingCqrs.Domain.Abstractions;

// The persistence port for command deduplication (ADR 0016). The two methods
// implement the eager-check-with-lazy-fallback flow: IdempotencyBehavior calls
// ExistsAsync before dispatching (the eager check) and TryRecordAsync after the
// handler succeeds (the record). The key is recorded only on success, so a
// failed command stays retryable. TryRecordAsync's return value distinguishes a
// first write from a race-loss, the lazy fallback for two dispatches that both
// pass the eager check before either records.
public interface IIdempotencyStore
{
    // Eager check: has a command with this key already been recorded?
    Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct);

    // Records the key after a successful dispatch. Returns true on a first
    // write, false if the key was already present (the race-loss case).
    Task<bool> TryRecordAsync(string idempotencyKey, string commandType, CancellationToken ct);
}
