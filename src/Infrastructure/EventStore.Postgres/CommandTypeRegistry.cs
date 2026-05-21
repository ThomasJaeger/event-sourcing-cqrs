using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Maps storage-side command type names to CLR types and back, parallel to
// EventTypeRegistry but constrained to ICommand. The delay queue stores a
// command as command_type plus a JSON command_payload (ADR 0017); the type name
// written on schedule resolves back to the CLR type for deserialization on
// dispatch. Same two-ordinal-dictionary, throw-on-duplicate, throw-on-unknown
// shape as the event registries so the three read alike.
public sealed class CommandTypeRegistry
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];

    public CommandTypeRegistry Register<TCommand>() where TCommand : ICommand
        => Register(typeof(TCommand));

    public CommandTypeRegistry Register<TCommand>(string typeName) where TCommand : ICommand
        => Register(typeof(TCommand), typeName);

    public CommandTypeRegistry Register(Type commandType)
    {
        ArgumentNullException.ThrowIfNull(commandType);
        return Register(commandType, commandType.Name);
    }

    public CommandTypeRegistry Register(Type commandType, string typeName)
    {
        ArgumentNullException.ThrowIfNull(commandType);
        ArgumentException.ThrowIfNullOrEmpty(typeName);

        // The generic overloads enforce ICommand at compile time; the non-generic
        // path that ICommandTypeProvider walks needs the same guarantee at
        // runtime, otherwise a typo in a provider would land a non-command type
        // in the registry and surface much later.
        if (!typeof(ICommand).IsAssignableFrom(commandType))
        {
            throw new ArgumentException(
                $"Type '{commandType.FullName}' does not implement ICommand.",
                nameof(commandType));
        }

        if (_byName.TryGetValue(typeName, out var existingType))
        {
            throw new InvalidOperationException(
                $"Command type name '{typeName}' is already registered to '{existingType.FullName}'. " +
                $"Conflicting registration: '{commandType.FullName}'.");
        }
        if (_byType.TryGetValue(commandType, out var existingName))
        {
            throw new InvalidOperationException(
                $"CLR type '{commandType.FullName}' is already registered under name '{existingName}'. " +
                $"Conflicting registration: '{typeName}'.");
        }

        _byName.Add(typeName, commandType);
        _byType.Add(commandType, typeName);
        return this;
    }

    public string NameFor(Type commandType)
    {
        ArgumentNullException.ThrowIfNull(commandType);
        if (!_byType.TryGetValue(commandType, out var name))
        {
            throw new UnknownCommandTypeException(commandType.FullName ?? commandType.Name);
        }
        return name;
    }

    public Type TypeFor(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        if (!_byName.TryGetValue(typeName, out var type))
        {
            throw new UnknownCommandTypeException(typeName);
        }
        return type;
    }
}
