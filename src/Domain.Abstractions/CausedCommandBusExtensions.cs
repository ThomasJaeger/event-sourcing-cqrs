namespace EventSourcingCqrs.Domain.Abstractions;

// TrySendAsync wraps ICausedCommandBus.SendAsync so a process-manager handler
// receives a CommandOutcome instead of an exception for the expected
// dispatch-failure modes (ADR 0015). DomainException (an aggregate rejected the
// command) and ConcurrencyException (the command lost an optimistic-concurrency
// race) become Failed outcomes the handler branches on. Any other exception is
// a fault, not a workflow signal: it propagates, and the outbox redelivers the
// triggering event so the whole handler retries.
public static class CausedCommandBusExtensions
{
    public static async Task<CommandOutcome> TrySendAsync(
        this ICausedCommandBus bus,
        ICommand command,
        EventMetadata causingEventMetadata,
        SystemActor actor,
        string? idempotencyKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bus);
        try
        {
            await bus.SendAsync(command, causingEventMetadata, actor, idempotencyKey, ct);
            return CommandOutcome.Success();
        }
        catch (DomainException ex)
        {
            return CommandOutcome.Failed(ex);
        }
        catch (ConcurrencyException ex)
        {
            // ConcurrencyException is classified as expected to keep parallel
            // fan-out complete, not because it is permanent; see ADR 0015's
            // Trigger section for the residual tension.
            return CommandOutcome.Failed(ex);
        }
    }
}
