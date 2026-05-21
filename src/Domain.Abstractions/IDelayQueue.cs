namespace EventSourcingCqrs.Domain.Abstractions;

// Schedules a command to dispatch at a future time and cancels pending
// schedules (ADR 0017). A process manager schedules a timeout on entering an
// awaiting state and cancels it when the awaited event arrives; if the event
// never arrives, the command fires. ScheduleAsync carries the full dispatch
// metadata so the stored row is self-describing and DelayQueueProcessor can
// rebuild the command context (ADR 0014) without a lookup. ScheduleAsync
// requires an idempotency key: a redelivered timeout deduplicates through the
// pipeline (ADR 0016), which is what makes the row-lock claim safe.
public interface IDelayQueue
{
    Task ScheduleAsync(
        ICommand command,
        DateTimeOffset fireAtUtc,
        StreamId scheduledByStream,
        string scheduledByStep,
        EventMetadata causingEventMetadata,
        SystemActor actor,
        string idempotencyKey,
        CancellationToken ct);

    // Cancels every pending (not dispatched, not already cancelled) schedule for
    // the stream and step. Returns true if any row was cancelled, false if none
    // matched. Cancelling a step cancels all its pending timeouts; the
    // idempotency key is the dedup key, not the cancel key.
    Task<bool> CancelAsync(
        StreamId scheduledByStream,
        string scheduledByStep,
        string cancellationReason,
        CancellationToken ct);
}
