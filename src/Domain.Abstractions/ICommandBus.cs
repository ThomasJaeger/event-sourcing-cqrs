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

    // The authenticated user-dispatch path (Phase 9). The Api /commands endpoint resolves the actor
    // and its authoritative roles from the request and dispatches here, so both land on the command
    // context and the actor lands on event metadata. Correlation and causation are minted fresh as
    // on the bare overloads; the idempotency key threads through as on the key overload. On the
    // interface for the same reason that overload is: the Api host resolves ICommandBus through DI.
    // The bare overloads keep ActorId = Guid.Empty for callers without a principal.
    Task SendAsync(
        ICommand command,
        Guid actorId,
        IReadOnlyCollection<Role> roles,
        TenantId tenant,
        string? idempotencyKey,
        CancellationToken ct);
}
