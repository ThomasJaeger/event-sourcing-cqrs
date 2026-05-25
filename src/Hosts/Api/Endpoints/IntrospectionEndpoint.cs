using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.Api.Endpoints;

// GET /commands and GET /queries: the introspection surface for the by-name
// dispatch endpoints. Each returns the catalog of type discriminators the
// matching POST endpoint accepts, so a client can discover the valid envelope
// tokens. The command catalog is the Api host's 14 user commands (the per-host
// registry composition excludes PM-dispatched and timeout commands, Commit 11);
// the query catalog is the 5 read models. Both map the registry's Type set
// through NameFor to the token strings, sorted for a deterministic response.
// Synchronous: the registries are in-memory and the read touches no I/O.
public static class IntrospectionEndpoint
{
    public static IResult ListCommands(CommandTypeRegistry registry)
        => Results.Ok(Tokens(registry.EnumerateCommands(), registry.NameFor));

    public static IResult ListQueries(QueryTypeRegistry registry)
        => Results.Ok(Tokens(registry.EnumerateQueries(), registry.NameFor));

    private static string[] Tokens(IEnumerable<Type> types, Func<Type, string> nameFor)
        => types.Select(nameFor).OrderBy(name => name, StringComparer.Ordinal).ToArray();
}
