using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Maps storage-side event type names to CLR types and back. Two ordinal
// dictionaries because the storage name is an identifier, not natural
// language. Duplicate registration throws at the registration site so a
// composition-root misconfiguration surfaces at startup rather than as a
// silent overwrite at first use. Unknown lookups throw rather than return
// null so callers do not pass a null Type to a JSON deserializer.
//
// Pattern from Chapter 11. Slated to move to Infrastructure/Versioning
// when that project is created in Phase 12 alongside the upcaster
// pipeline; lives here for now because the PostgreSQL adapter is the
// only consumer.
public sealed class EventTypeRegistry
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];

    public EventTypeRegistry Register<TEvent>() where TEvent : IDomainEvent
        => Register<TEvent>(typeof(TEvent).Name);

    public EventTypeRegistry Register<TEvent>(string typeName) where TEvent : IDomainEvent
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        var type = typeof(TEvent);

        if (_byName.TryGetValue(typeName, out var existingType))
        {
            throw new InvalidOperationException(
                $"Event type name '{typeName}' is already registered to '{existingType.FullName}'. " +
                $"Conflicting registration: '{type.FullName}'.");
        }
        if (_byType.TryGetValue(type, out var existingName))
        {
            throw new InvalidOperationException(
                $"CLR type '{type.FullName}' is already registered under name '{existingName}'. " +
                $"Conflicting registration: '{typeName}'.");
        }

        _byName.Add(typeName, type);
        _byType.Add(type, typeName);
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
