using System.Security.Claims;
using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace EventSourcingCqrs.Hosts.Api.Endpoints;

// POST /commands: the single command-dispatch transport (D3). The type
// discriminator resolves to a CLR command type through CommandTypeRegistry
// (ADR 0023); the payload deserializes against it with the same web-default JSON
// options the envelope bound with. The client-generated Idempotency-Key threads
// into the command pipeline (ADR 0021). Envelope and header validation return 400
// here; dispatch-time exceptions bubble to ExceptionMappingMiddleware.
public static class CommandsEndpoint
{
    public static async Task<IResult> HandleAsync(
        CommandEnvelope envelope,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        CommandTypeRegistry registry,
        ICommandBus bus,
        IPrincipalFactory principalFactory,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Results.BadRequest(new { code = "MISSING_IDEMPOTENCY_KEY",
                message = "The Idempotency-Key header is required." });
        }
        if (string.IsNullOrWhiteSpace(envelope.Type))
        {
            return Results.BadRequest(new { code = "MISSING_TYPE",
                message = "The command envelope's type is required." });
        }

        Type commandType;
        try
        {
            commandType = registry.TypeFor(envelope.Type);
        }
        catch (UnknownCommandTypeException)
        {
            return Results.BadRequest(new { code = "UNKNOWN_TYPE",
                message = $"'{envelope.Type}' is not a known command type." });
        }

        ICommand? command;
        try
        {
            command = (ICommand?)envelope.Payload.Deserialize(
                commandType, jsonOptions.Value.SerializerOptions);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { code = "MALFORMED_PAYLOAD", message = ex.Message });
        }
        if (command is null)
        {
            return Results.BadRequest(new { code = "MALFORMED_PAYLOAD",
                message = $"The payload could not be read as '{envelope.Type}'." });
        }

        // The route requires authentication, so the principal is established and carries the actor
        // id as its name identifier. Load the actor's authoritative roles (not the forwarded claim)
        // and dispatch through the principal overload so the actor lands on event metadata.
        var actorClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(actorClaim, out var actorId))
        {
            return Results.Unauthorized();
        }
        var principal = await principalFactory.CreateAsync(actorId, ct);

        await bus.SendAsync(command, principal.ActorId, principal.Roles, principal.Tenant, idempotencyKey, ct);
        return Results.Accepted(value: new CommandAcceptedResponse(DateTime.UtcNow));
    }
}
