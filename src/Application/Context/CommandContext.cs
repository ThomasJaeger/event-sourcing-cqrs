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

    public required string ServiceName { get; init; }

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
