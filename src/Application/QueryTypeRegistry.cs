using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application;

// Maps transport-side query type tokens to CLR types and back, parallel in shape
// to the three event-store type registries but placed here rather than in
// EventStore.Postgres because the concern is transport, not persistence: the
// /queries HTTP endpoint round-trips an envelope discriminator, and queries never
// persist (ADR 0022). Same two-ordinal-dictionary, throw-on-duplicate,
// throw-on-unknown shape as the event and command registries. Two places the
// parallel is shape-only: IQuery<TResult> has no non-generic marker, so the
// type-family guard is a reflection check rather than IsAssignableFrom; and the
// GET /queries introspection endpoint needs the catalog, so EnumerateQueries is
// exposed where the persistence registries need no enumeration.
public sealed class QueryTypeRegistry
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];

    public QueryTypeRegistry Register(Type queryType)
    {
        ArgumentNullException.ThrowIfNull(queryType);
        return Register(queryType, queryType.Name);
    }

    public QueryTypeRegistry Register(Type queryType, string typeName)
    {
        ArgumentNullException.ThrowIfNull(queryType);
        ArgumentException.ThrowIfNullOrEmpty(typeName);

        // The event-store registries constrain a generic Register<T>() on a
        // non-generic marker (IDomainEvent, ICommand) and guard the non-generic
        // path with IsAssignableFrom. IQuery<TResult> is generic with no
        // non-generic marker, so this registry has no generic overload and guards
        // the Type path by checking the type closes IQuery<> (ADR 0022).
        if (!ImplementsQueryInterface(queryType))
        {
            throw new ArgumentException(
                $"Type '{queryType.FullName}' does not implement IQuery<TResult>.",
                nameof(queryType));
        }

        if (_byName.TryGetValue(typeName, out var existingType))
        {
            throw new InvalidOperationException(
                $"Query type name '{typeName}' is already registered to '{existingType.FullName}'. " +
                $"Conflicting registration: '{queryType.FullName}'.");
        }
        if (_byType.TryGetValue(queryType, out var existingName))
        {
            throw new InvalidOperationException(
                $"CLR type '{queryType.FullName}' is already registered under name '{existingName}'. " +
                $"Conflicting registration: '{typeName}'.");
        }

        _byName.Add(typeName, queryType);
        _byType.Add(queryType, typeName);
        return this;
    }

    public string NameFor(Type queryType)
    {
        ArgumentNullException.ThrowIfNull(queryType);
        if (!_byType.TryGetValue(queryType, out var name))
        {
            throw new UnknownQueryTypeException(queryType.FullName ?? queryType.Name);
        }
        return name;
    }

    public Type TypeFor(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        if (!_byName.TryGetValue(typeName, out var type))
        {
            throw new UnknownQueryTypeException(typeName);
        }
        return type;
    }

    // The GET /queries introspection endpoint reads the registered query catalog
    // and maps each type to its token through NameFor (ADR 0022). The three
    // persistence registries expose no equivalent because no persistence consumer
    // needs the catalog. The registry is built once at startup and not mutated
    // after, so returning the live key view is safe.
    public IReadOnlyCollection<Type> EnumerateQueries() => _byType.Keys;

    private static bool ImplementsQueryInterface(Type type)
        => type.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuery<>));
}
