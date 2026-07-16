using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Versioning;

// Maps storage-side PM event type names to CLR types and back, parallel to
// EventTypeRegistry but constrained to IProcessManagerEvent. PM events persist
// to the same events table as aggregate events (ADR 0013) and resolve through
// this separate registry, selected by the typed read method rather than by
// per-row inspection.
//
// Consolidated here from the three adapters per ADR 0048; each carried a
// byte-identical copy. Kept separate from EventTypeRegistry rather than merged:
// the two registries answer different questions and ADR 0013's type hierarchy
// keeps aggregate and PM events apart at the type level.
public sealed class ProcessManagerEventTypeRegistry
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];

    public ProcessManagerEventTypeRegistry Register<TEvent>() where TEvent : IProcessManagerEvent
        => Register(typeof(TEvent));

    public ProcessManagerEventTypeRegistry Register<TEvent>(string typeName) where TEvent : IProcessManagerEvent
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
