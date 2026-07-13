using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// The PM twin of EventTypeRegistry, constrained to IProcessManagerEvent. PM events persist to
// the same events table as aggregate events (ADR 0013) and resolve through this separate
// registry, selected by the typed read method rather than by per-row inspection.
//
// Duplicated from the PostgreSQL adapter per ADR 0004, same as its aggregate twin.
public sealed class ProcessManagerEventTypeRegistry
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];

    public ProcessManagerEventTypeRegistry Register<TEvent>() where TEvent : IProcessManagerEvent
        => Register(typeof(TEvent));

    public ProcessManagerEventTypeRegistry Register<TEvent>(string typeName)
        where TEvent : IProcessManagerEvent
        => Register(typeof(TEvent), typeName);

    public ProcessManagerEventTypeRegistry Register(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return Register(eventType, eventType.Name);
    }

    public ProcessManagerEventTypeRegistry Register(Type eventType, string typeName)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentException.ThrowIfNullOrEmpty(typeName);

        if (!typeof(IProcessManagerEvent).IsAssignableFrom(eventType))
        {
            throw new ArgumentException(
                $"Type '{eventType.FullName}' does not implement IProcessManagerEvent.",
                nameof(eventType));
        }

        if (_byName.TryGetValue(typeName, out var existingType))
        {
            throw new InvalidOperationException(
                $"PM event type name '{typeName}' is already registered to '{existingType.FullName}'. " +
                $"Conflicting registration: '{eventType.FullName}'.");
        }
        if (_byType.TryGetValue(eventType, out var existingName))
        {
            throw new InvalidOperationException(
                $"CLR type '{eventType.FullName}' is already registered under name '{existingName}'. " +
                $"Conflicting registration: '{typeName}'.");
        }

        _byName.Add(typeName, eventType);
        _byType.Add(eventType, typeName);
        return this;
    }

    public string NameFor(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        if (!_byType.TryGetValue(eventType, out var name))
        {
            throw new UnknownEventTypeException(eventType.FullName ?? eventType.Name);
        }
        return name;
    }

    public Type TypeFor(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        if (!_byName.TryGetValue(typeName, out var type))
        {
            throw new UnknownEventTypeException(typeName);
        }
        return type;
    }
}
