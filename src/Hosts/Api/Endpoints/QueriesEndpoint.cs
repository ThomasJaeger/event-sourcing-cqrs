using System.Reflection;
using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace EventSourcingCqrs.Hosts.Api.Endpoints;

// POST /queries: the single query-dispatch transport, parallel to the /commands
// endpoint. The type discriminator resolves to a CLR query type through
// QueryTypeRegistry (ADR 0022); the payload deserializes against it with the same
// web-default JSON options the envelope bound with. Three divergences from
// /commands, all because a query is not a command: no Idempotency-Key header
// (ADR 0021's threading is scoped to the user-dispatch command path; a query is
// read-only and idempotent by nature), a 200-with-body-or-404-on-null disposition
// instead of a 202 ack, and reflection-based dispatch (below).
public static class QueriesEndpoint
{
    public static async Task<IResult> HandleAsync(
        QueryEnvelope envelope,
        QueryTypeRegistry registry,
        IQueryBus bus,
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

        var result = await DispatchAsync(bus, query, queryType, ct);
        // Null is the not-found disposition for the nullable single-row and composed
        // views; the list queries never return null, so an empty result is still a
        // 200 with an empty array, never a 404.
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static readonly MethodInfo AskMethod =
        typeof(IQueryBus).GetMethod(nameof(IQueryBus.AskAsync))!;

    // IQueryBus.AskAsync is generic over the query's result type, and the
    // type-discriminated envelope resolves only the query type, not the result
    // type, so AskAsync<TResult> cannot be called by inference here. Read TResult
    // off the IQuery<TResult> the resolved type closes, bind AskAsync to it, and
    // invoke. The alternative, a non-generic AskAsync on IQueryBus, would parallel
    // ADR 0021's ICommandBus widening; it is declined because a query's result type
    // is part of its contract, so the generic surface stays and this transport edge
    // bridges to it rather than widening the port for one consumer.
    private static async Task<object?> DispatchAsync(
        IQueryBus bus, object query, Type queryType, CancellationToken ct)
    {
        var resultType = queryType.GetInterfaces()
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuery<>))
            .GetGenericArguments()[0];
        var task = (Task)AskMethod.MakeGenericMethod(resultType).Invoke(bus, [query, ct])!;
        await task;
        // The task has completed, so reading Task<TResult>.Result does not block.
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
}
