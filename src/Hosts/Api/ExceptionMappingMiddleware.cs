using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.Api;

// Translates the dispatch-time exceptions the command pipeline raises into HTTP
// status codes with structured bodies (D5). The endpoint handles envelope and
// header validation itself; this middleware handles what bubbles out of the
// command bus. The unknown-exception path returns a generic 500 that does not leak
// the exception's detail. Convention-based middleware so app.UseMiddleware
// constructs it with the next delegate; it holds no per-request state.
public sealed class ExceptionMappingMiddleware
{
    private readonly RequestDelegate _next;

    public ExceptionMappingMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException ex)
        {
            var errors = ex.Errors
                .GroupBy(e => e.Path)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new HttpValidationProblemDetails(errors));
        }
        catch (DomainException ex)
        {
            await WriteError(context, StatusCodes.Status422UnprocessableEntity,
                new { code = "RULE_VIOLATED", message = ex.Message });
        }
        catch (ConcurrencyException ex)
        {
            await WriteError(context, StatusCodes.Status409Conflict,
                new { code = "CONCURRENCY", message = ex.Message, expectedVersion = ex.ExpectedVersion });
        }
        catch (AggregateNotFoundException ex)
        {
            await WriteError(context, StatusCodes.Status404NotFound,
                new { code = "NOT_FOUND", message = ex.Message, aggregateId = ex.AggregateId });
        }
        catch (UnauthorizedCommandException ex)
        {
            await WriteError(context, StatusCodes.Status403Forbidden,
                new { code = "FORBIDDEN", message = ex.Message });
        }
        catch (UnauthorizedQueryException ex)
        {
            await WriteError(context, StatusCodes.Status403Forbidden,
                new { code = "FORBIDDEN", message = ex.Message });
        }
        catch (Exception)
        {
            // The 500 body is deliberately generic: a leaked exception message can
            // disclose internals. Operators read the logged exception, not the body.
            await WriteError(context, StatusCodes.Status500InternalServerError,
                new { code = "INTERNAL", message = "An error occurred. Please try again." });
        }
    }

    private static Task WriteError(HttpContext context, int statusCode, object body)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(body);
    }
}
