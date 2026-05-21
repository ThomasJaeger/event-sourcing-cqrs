using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application;

// The process-manager-facing command-dispatch surface (ADR 0014). Translates a
// causing event's metadata into the context the new command carries: the
// correlation id flows so the trace stays one workflow, the causing event's
// EventId becomes the command's causation source, and the SystemActor names the
// process manager in audit logs and the Phase 9 Correlation-ID Tracer. The work
// runs through CommandBus's internal SendWithContextAsync seam, so the command
// goes through the same scope, pipeline, and accessor machinery the user path
// uses (ADR 0014 forbids a parallel dispatcher that would diverge on behavior).
//
// Takes the concrete CommandBus rather than ICommandBus: the seam is internal
// to this assembly and is deliberately not part of the public bus contract.
public sealed class CausedCommandBus : ICausedCommandBus
{
    private readonly CommandBus _bus;

    public CausedCommandBus(CommandBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    public Task SendAsync(
        ICommand command,
        EventMetadata causingEventMetadata,
        SystemActor actor,
        string? idempotencyKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(causingEventMetadata);
        ArgumentNullException.ThrowIfNull(actor);

        // Causation is event-to-event for a process-manager-originated command:
        // the new command's CausationCommandId points at the causing event's
        // EventId (ADR 0014). CorrelationId carries forward unchanged.
        var fragment = new CausedDispatchFragment(
            CorrelationId: causingEventMetadata.CorrelationId,
            CausationCommandId: causingEventMetadata.EventId,
            ActorId: actor.Id,
            ServiceName: actor.ServiceName,
            IdempotencyKey: idempotencyKey);

        return _bus.SendWithContextAsync(command, fragment, ct);
    }
}
