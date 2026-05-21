namespace EventSourcingCqrs.Domain.Abstractions;

// Each component that schedules commands to the delay queue registers the
// command types it owns through an ICommandTypeProvider, parallel to
// IEventTypeProvider for aggregate events. Pull-shape: the provider declares the
// command types it owns; AddPostgresEventStore walks the providers and registers
// each into the CommandTypeRegistry. The delay queue stores a command by type
// name (ADR 0017), so a scheduled command's type must be registered for the row
// to round-trip back to a concrete command on dispatch.
public interface ICommandTypeProvider
{
    IEnumerable<Type> GetCommandTypes();
}
