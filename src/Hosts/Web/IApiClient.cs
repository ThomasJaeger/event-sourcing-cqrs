using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.Web;

// The Web host's only outbound dependency on the Api host (D-W). Every Web page
// that dispatches a command or runs a query goes through this interface; no page
// touches HttpClient, the type-discriminator envelope shape, or the
// Idempotency-Key header directly. Implemented by ApiClient as a typed HttpClient
// against the Api host's POST /commands and POST /queries endpoints (D3).
public interface IApiClient
{
    // Dispatches a command. The idempotencyKey is generated at the call site (D2:
    // UUID v4 at button-click time, one per click; the same key reused across an
    // infrastructure-failure retry inside the optimistic-UI window) and travels in
    // the Idempotency-Key HTTP header (ADR 0021's threading on the user-dispatch
    // path). Returns the 202 Accepted body so the caller can record AcceptedUtc
    // for the optimistic-UI timing thresholds (D6) and the polling window (D7).
    Task<CommandAcceptedResponse> SendCommandAsync(
        ICommand command,
        string idempotencyKey,
        CancellationToken ct);

    // Runs a query. The result type closes the IQuery<TResult> contract; null is
    // the not-found disposition for the nullable single-row and composed views,
    // matching the Api host's QueriesEndpoint disposition (200-with-body or 404
    // returned as null here). List queries never return null; an empty list is
    // still a non-null result.
    Task<TResult?> QueryAsync<TResult>(
        IQuery<TResult> query,
        CancellationToken ct);
}
