namespace EventSourcingCqrs.Domain.Abstractions;

// Process managers and host edges call SendAsync; the bus resolves the handler
// for the runtime command type, runs the pipeline, and invokes the handler in
// a new service scope per command.
public interface ICommandBus
{
    Task SendAsync(ICommand command, CancellationToken ct);

    // Threads a caller-supplied idempotency key into the command context so
    // IdempotencyBehavior can dedupe a retried command (ADR 0021). Phase 7's
    // HTTP edges (the Api /commands endpoint, the Web IApiClient) supply a
    // client-generated key; a null key behaves exactly like the bare overload.
    // This widens the interface past the Ch 10 shape because the Api host
    // resolves ICommandBus through DI and dispatches every user command through
    // it.
    Task SendAsync(ICommand command, string? idempotencyKey, CancellationToken ct);
}
