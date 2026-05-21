namespace EventSourcingCqrs.Application;

// The context values CausedCommandBus hands the CommandBus seam in place of the
// freshly-minted ones the user-dispatch path uses (ADR 0014). internal because
// the seam that consumes it is internal; process-manager handlers never see
// this type. They pass an EventMetadata and a SystemActor to ICausedCommandBus
// and the bus builds the fragment.
internal sealed record CausedDispatchFragment(
    Guid CorrelationId,
    Guid CausationCommandId,
    Guid ActorId,
    string ServiceName,
    string? IdempotencyKey);
