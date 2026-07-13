using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Maps storage-side event type names to CLR types and back. Duplicated from the PostgreSQL
// adapter rather than shared, per ADR 0004: adapters are self-contained and no cross-adapter
// layer exists for them to sit in. The duplication is the ADR's accepted cost.
//
// Pattern from Chapter 11. Slated to move to Infrastructure/Versioning when that project is
// created in Phase 15, at which point the two copies collapse into one.
public sealed class EventTypeRegistry
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];

    public EventTypeRegistry Register<TEvent>() where TEvent : IDomainEvent
        => Register(typeof(TEvent));

    public EventTypeRegistry Register<TEvent>(string typeName) where TEvent : IDomainEvent
        => Register(typeof(TEvent), typeName);

    public EventTypeRegistry Register(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return Register(eventType, eventType.Name);
    }

    public EventTypeRegistry Register(Type eventType, string typeName)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentException.ThrowIfNullOrEmpty(typeName);

        if (!typeof(IDomainEvent).IsAssignableFrom(eventType))
        {
            throw new ArgumentException(
                $"Type '{eventType.FullName}' does not implement IDomainEvent.",
                nameof(eventType));
        }

        if (_byName.TryGetValue(typeName, out var existingType))
        {
            throw new InvalidOperationException(
                $"Event type name '{typeName}' is already registered to '{existingType.FullName}'. " +
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
