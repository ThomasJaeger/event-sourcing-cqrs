namespace EventSourcingCqrs.Domain.Abstractions;

// AsyncLocal-backed in production so a singleton (the event serializer) can
// read the context the bus sets on the current async flow. Mirrors the
// IHttpContextAccessor pattern. Returns null when no command is in flight;
// system-emitted writes (projection workers, outbox processor) fall back to
// CommandContext.System inside the serializer.
public interface ICommandContextAccessor
{
    ICommandContext? Current { get; set; }
}
