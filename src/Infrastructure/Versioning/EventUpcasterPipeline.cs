using System.Reflection;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Versioning;

// Read-time upcasting pipeline (Chapter 11: Upcasting). Resolves a stored (storage name, schema
// version) to the CLR type that shape was written as, and lifts a deserialized event through its
// lineage's upcaster chain to the current shape. Schema versions are derived from chain topology,
// never declared: the registry's current type is the terminal, and each upcaster link steps one
// version back from it, so version 1 is the oldest shape and the terminal is the current version.
//
// Links are indexed once at construction and each carries a compiled delegate (bound to the
// upcaster's non-generic bridge), so the read path performs no per-event reflection. Construction
// rejects a malformed link set loudly: a non-upcaster element, a branch or a merge that would make
// the chain non-linear, a cycle, and a chain that reaches no registered terminal.
public sealed class EventUpcasterPipeline
{
    private readonly EventTypeRegistry _registry;
    private readonly Dictionary<Type, Link> _byFrom = [];
    private readonly Dictionary<Type, Link> _byTo = [];

    public EventUpcasterPipeline(EventTypeRegistry registry, IEnumerable<object> upcasters)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(upcasters);
        _registry = registry;

        var links = new List<Link>();
        foreach (var upcaster in upcasters)
        {
            ArgumentNullException.ThrowIfNull(upcaster);
            var baseType = UpcasterBaseOf(upcaster.GetType());
            if (baseType is null)
            {
                throw new InvalidOperationException(
                    $"Upcaster collection element of type '{upcaster.GetType().FullName}' " +
                    "is not an Upcaster<TFrom, TTo>.");
            }
            links.Add(CreateLink(upcaster, baseType));
        }

        // A shape has at most one upcaster and at most one producer, so the graph is linear.
        foreach (var link in links)
        {
            if (!_byFrom.TryAdd(link.FromType, link))
            {
                throw new InvalidOperationException(
                    $"Two upcasters share the source type '{link.FromType.Name}'; " +
                    "a shape may be upcast by at most one link.");
            }
            if (!_byTo.TryAdd(link.ToType, link))
            {
                throw new InvalidOperationException(
                    $"Two upcasters produce the target type '{link.ToType.Name}'; " +
                    "a shape may be produced by at most one link.");
            }
        }

        // Every chain ends at a type the registry knows; a chain that stops short of a registered
        // terminal is a gap or an orphan.
        foreach (var link in links)
        {
            var terminal = TerminalOf(link.FromType);
            if (!IsRegistered(terminal))
            {
                throw new InvalidOperationException(
                    $"Upcaster chain ending at '{terminal.Name}' reaches no registered event " +
                    "type; register the terminal shape or complete the chain to one.");
            }
        }
    }

    // The CLR type a stored (name, version) was written as. The current version is the registered
    // type; earlier versions are the source shapes of the chain, counted back from the terminal.
    public Type ResolveType(string storageName, int storedVersion)
    {
        var chain = ChainFor(storageName);
        GuardVersion(storageName, storedVersion, chain.Count);
        return chain[storedVersion - 1];
    }

    // Lifts a deserialized event from its stored version to the current shape by applying each link
    // in order. At the current version there is nothing to apply, so the same instance returns.
    public IDomainEvent Upcast(string storageName, int storedVersion, object @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var chain = ChainFor(storageName);
        GuardVersion(storageName, storedVersion, chain.Count);

        var current = (IDomainEvent)@event;
        for (var i = storedVersion - 1; i < chain.Count - 1; i++)
        {
            current = _byFrom[chain[i]].Invoke(current);
        }
        return current;
    }

    // The current schema version for a storage name: the number of shapes in its chain, which is
    // the link count plus one. A registered name with no upcasters is version 1.
    public int CurrentVersionFor(string storageName)
        => ChainFor(storageName).Count;

    // The ordered shapes for a storage name, oldest first, ending at the registered current type.
    // An unknown name fails through the registry's UnknownEventTypeException.
    private IReadOnlyList<Type> ChainFor(string storageName)
    {
        var terminal = _registry.TypeFor(storageName);
        var chain = new List<Type> { terminal };
        var cursor = terminal;
        while (_byTo.TryGetValue(cursor, out var link))
        {
            chain.Insert(0, link.FromType);
            cursor = link.FromType;
        }
        return chain;
    }

    private static void GuardVersion(string storageName, int storedVersion, int currentVersion)
    {
        if (storedVersion < 1 || storedVersion > currentVersion)
        {
            throw new UnknownEventSchemaVersionException(storageName, storedVersion);
        }
    }

    // Walks forward from a source shape to the shape no link consumes: the chain's terminal.
    private Type TerminalOf(Type fromType)
    {
        var cursor = fromType;
        var seen = new HashSet<Type>();
        while (_byFrom.TryGetValue(cursor, out var link))
        {
            if (!seen.Add(cursor))
            {
                throw new InvalidOperationException(
                    $"Upcaster links form a cycle through '{cursor.Name}'.");
            }
            cursor = link.ToType;
        }
        return cursor;
    }

    // Probes the registry through its own thrown contract rather than a bespoke membership method,
    // so the shared registry gains no surface for this one construction-time check.
    private bool IsRegistered(Type eventType)
    {
        try
        {
            _registry.NameFor(eventType);
            return true;
        }
        catch (UnknownEventTypeException)
        {
            return false;
        }
    }

    private static Link CreateLink(object upcaster, Type baseType)
    {
        var typeArgs = baseType.GetGenericArguments();
        var bridge = baseType.GetMethod(
            nameof(Upcaster<IDomainEvent, IDomainEvent>.UpcastUntyped),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Upcaster<TFrom, TTo>.UpcastUntyped is missing; the pipeline cannot bind its invoker.");
        var invoke = bridge.CreateDelegate<Func<IDomainEvent, IDomainEvent>>(upcaster);
        return new Link(typeArgs[0], typeArgs[1], invoke);
    }

    private static Type? UpcasterBaseOf(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Upcaster<,>))
            {
                return current;
            }
        }
        return null;
    }

    private sealed record Link(Type FromType, Type ToType, Func<IDomainEvent, IDomainEvent> Invoke);
}
