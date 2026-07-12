namespace EventSourcingCqrs.Domain.Abstractions;

// The command context a process-manager handler writes under when the outbox dispatches an event to
// it (ADR 0042). A dispatched command gets its context from CommandBus; an event dispatch has no
// command and no bus, so the dispatcher builds this from the causing event's metadata and the
// handler's declared actor. ProcessManagerRepository stamps the PM's own events off the ambient
// context, so without one it falls back to empty correlation, causation, and actor, and the PM's
// rows drop out of the workflow's trace.
//
// Correlation carries the workflow forward unchanged. Causation is event-to-event: it points at the
// EventId of the event that caused the save, the shape ADR 0014 set for PM-dispatched commands,
// applied here to the PM's own save.
//
// Roles and AuthorizationMode mirror what CommandBus.SendWithContextAsync hard-sets for system
// dispatch, so a PM writing on the outbox route and a PM writing under a resurfaced timeout command
// carry the same authorization shape. IdempotencyKey is null: an event dispatch carries no command
// key.
public sealed record CausedCommandContext : ICommandContext
{
    private readonly TimeProvider _timeProvider;

    public CausedCommandContext(
        EventMetadata causingEventMetadata, SystemActor actor, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(causingEventMetadata);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        CorrelationId = causingEventMetadata.CorrelationId;
        CausationCommandId = causingEventMetadata.EventId;
        ActorId = actor.Id;
        ServiceName = actor.ServiceName;
    }

    public Guid CorrelationId { get; }

    public Guid CausationCommandId { get; }

    public Guid ActorId { get; }

    public IReadOnlyCollection<Role> Roles => SystemActor.SystemRoles;

    public DispatchAuthorizationMode AuthorizationMode => DispatchAuthorizationMode.SystemActor;

    public string ServiceName { get; }

    public string? IdempotencyKey => null;

    public DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();
}
