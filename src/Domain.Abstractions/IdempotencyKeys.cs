namespace EventSourcingCqrs.Domain.Abstractions;

// Deterministic idempotency keys for commands a process manager dispatches
// (ADR 0016). The key derives from the PM's stream plus the workflow step, so a
// redelivered inbound event re-dispatches the same command with the same key and
// IdempotencyBehavior dedups it at the pipeline. The optional sub-id separates
// per-item fan-out dispatches that share a step name (one ReserveInventory per
// order line, keyed by LineId).
public static class IdempotencyKeys
{
    // Format: {stream.Value}:{step} or {stream.Value}:{step}:{subId:N}.
    // Fail-loud on a non-PM stream: only process managers derive keys this way,
    // and a wrong stream would silently break dedup.
    public static string ForProcessManager(StreamId stream, string step, Guid? subId = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        if (!stream.Value.StartsWith(StreamPrefixes.ProcessManagerPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"ForProcessManager requires a process-manager stream id (pm- prefix); got '{stream}'.",
                nameof(stream));
        }

        return subId is null
            ? $"{stream.Value}:{step}"
            : $"{stream.Value}:{step}:{subId.Value:N}";
    }
}
