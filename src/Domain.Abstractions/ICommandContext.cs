namespace EventSourcingCqrs.Domain.Abstractions;

// Carries the per-command ambient state EventMetadata.ForCommand needs to
// stamp correlation, causation, actor, and source onto every event the
// handler's aggregate raises. UtcNow is a method, not a property, so a clock
// abstraction can return the same instant for every read inside one command.
// ActorId is a Guid to match EventMetadata.ActorId; Phase 7's HTTP middleware
// maps an authenticated principal to a Guid before populating the context.
public interface ICommandContext
{
    Guid CorrelationId { get; }

    Guid CausationCommandId { get; }

    Guid ActorId { get; }

    string ServiceName { get; }

    DateTimeOffset UtcNow();
}
