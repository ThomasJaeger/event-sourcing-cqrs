using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace EventSourcingCqrs.Hosts.Api.Endpoints;

// POST /queries: the single query-dispatch transport, parallel to the /commands
// endpoint. The type discriminator resolves to a CLR query type through
// QueryTypeRegistry (ADR 0022); the payload deserializes against it with the same
// web-default JSON options the envelope bound with. The route requires
// authentication, so the endpoint resolves the actor and its authoritative roles the
// same way /commands does and dispatches through the principal-carrying AskAsync
// overload, so the query-authorization behavior and the ownership-filtering handlers
// see the asking actor (P9.5). Three divergences from /commands, all because a query
// is not a command: no Idempotency-Key header (ADR 0021's threading is scoped to the
// command path; a query is read-only and idempotent by nature), a
// 200-with-body-or-404-on-null disposition instead of a 202 ack, and reflection-based
// dispatch (below).
public static class QueriesEndpoint
{
    public static async Task<IResult> HandleAsync(
        QueryEnvelope envelope,
        ClaimsPrincipal user,
        QueryTypeRegistry registry,
        IQueryBus bus,
        IPrincipalFactory principalFactory,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(envelope.Type))
        {
            return Results.BadRequest(new { code = "MISSING_TYPE",
                message = "The query envelope's type is required." });
        }

        Type queryType;
        try
        {
            queryType = registry.TypeFor(envelope.Type);
        }
        catch (UnknownQueryTypeException)
        {
            return Results.BadRequest(new { code = "UNKNOWN_TYPE",
                message = $"'{envelope.Type}' is not a known query type." });
        }

        object? query;
        try
        {
            query = envelope.Payload.Deserialize(queryType, jsonOptions.Value.SerializerOptions);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { code = "MALFORMED_PAYLOAD", message = ex.Message });
        }
        if (query is null)
        {
            return Results.BadRequest(new { code = "MALFORMED_PAYLOAD",
                message = $"The payload could not be read as '{envelope.Type}'." });
        }

        // The route requires authentication, so the principal is established and carries the actor id as
        // its name identifier. Load the actor's authoritative roles (not the forwarded claim) and
        // dispatch through the principal overload so the authorization behavior and the
        // ownership-filtering handlers read the asking actor, mirroring CommandsEndpoint.
        var actorClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(actorClaim, out var actorId))
        {
            return Results.Unauthorized();
        }
        var principal = await principalFactory.CreateAsync(actorId, ct);

        var result = await DispatchAsync(bus, query, queryType, principal.ActorId, principal.Roles, ct);
        // Null is the not-found disposition for the nullable single-row and composed
        // views; the list queries never return null, so an empty result is still a
        // 200 with an empty array, never a 404.
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    // IQueryBus now exposes two AskAsync overloads, so GetMethod(name) is ambiguous. Select the
    // principal-carrying one by its four parameters (query, actorId, roles, ct); the bare overload has
    // two. The endpoint always dispatches authenticated, so the principal overload is the one to bind.
    private static readonly MethodInfo AskMethod = typeof(IQueryBus)
        .GetMethods()
        .Single(m => m.Name == nameof(IQueryBus.AskAsync) && m.GetParameters().Length == 4);

    // IQueryBus.AskAsync is generic over the query's result type, and the
    // type-discriminated envelope resolves only the query type, not the result
    // type, so AskAsync<TResult> cannot be called by inference here. Read TResult
    // off the IQuery<TResult> the resolved type closes, bind AskAsync to it, and
    // invoke with the actor and roles. The alternative, a non-generic AskAsync on
    // IQueryBus, would parallel ADR 0021's ICommandBus widening; it is declined
    // because a query's result type is part of its contract, so the generic surface
    // stays and this transport edge bridges to it rather than widening the port for
    // one consumer.
    private static async Task<object?> DispatchAsync(
        IQueryBus bus, object query, Type queryType, Guid actorId, IReadOnlyCollection<Role> roles,
        CancellationToken ct)
    {
        var resultType = queryType.GetInterfaces()
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuery<>))
            .GetGenericArguments()[0];
        var task = (Task)AskMethod.MakeGenericMethod(resultType).Invoke(bus, [query, actorId, roles, ct])!;
        await task;
        // The task has completed, so reading Task<TResult>.Result does not block.
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
}
