using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Context;

// One context per dispatch. The bus builds a new instance, pushes it onto the
// accessor for the duration of the pipeline, and lets it fall out of scope
// afterwards. Init-only properties keep the instance immutable for as long as
// any handler or behavior holds a reference. A future authorization-style
// behavior that needs mid-pipeline mutation gets its own seam rather than
// reopening these setters.
public sealed class CommandContext : ICommandContext
{
    private readonly TimeProvider _timeProvider;

    public CommandContext() : this(TimeProvider.System) { }

    public CommandContext(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public required Guid CorrelationId { get; init; }

    public required Guid CausationCommandId { get; init; }

    public required Guid ActorId { get; init; }

    // The actor's authoritative roles (Phase 9). Defaults to empty rather than required: only the
    // authenticated user-dispatch path supplies roles, so the bare overloads, the CausedCommandBus
    // seam, and the System fallback leave it empty without restating it.
    public IReadOnlyCollection<Role> Roles { get; init; } = [];

    // True only on the authenticated user-dispatch path. Defaults false, so the bare overloads, the
    // CausedCommandBus seam, and the System fallback leave it false; the authorization behavior treats
    // false as a pass-through.
    public bool IsAuthenticatedUserDispatch { get; init; }

    public required string ServiceName { get; init; }

    // Non-required: only the CausedCommandBus seam sets it (ADR 0014). The
    // user-dispatch path and the System fallback leave it null.
    public string? IdempotencyKey { get; init; }

    public DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();

    // Fallback context for writes that originate outside a command scope:
    // projection workers, the outbox processor, anything that stamps metadata
    // when no command is in flight. Carries deterministic stub values so the
    // envelopes still parse without special-casing system writes downstream.
    public static CommandContext System { get; } = new()
    {
        CorrelationId = Guid.Empty,
        CausationCommandId = Guid.Empty,
        ActorId = Guid.Empty,
        ServiceName = "Workers"
    };
}
