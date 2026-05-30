using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Authentication;

namespace EventSourcingCqrs.Hosts.Web;

// IApiClient's HTTP implementation. Builds the type-discriminator envelope from
// the registries (D3 shape: { type, payload }), serializes the payload against
// JsonSerializerOptions.Web (matching the Api host's Microsoft.AspNetCore.Http.Json
// default), sets Idempotency-Key on the command path. Deliberately symmetric with
// the test-only HttpClientCommandExtensions/HttpClientQueryExtensions in
// IntegrationTests; the test helpers prove the wire format, this production type
// consumes it with DI and lifetime management.
//
// Every request carries the circuit's signed forwarded identity (P9.3b): AttachForwardedIdentityAsync
// reads the actor from the circuit-scoped provider and signs it, so the Api host (which has mandated
// the signature since Commit 1) accepts the request. Signing happens here, inside a per-circuit
// resolved client, not in a shared DelegatingHandler that would cross circuits.
//
// Non-2xx responses map to the ApiClientException hierarchy by status code: 400 to
// ApiValidationException, 422 to ApiBusinessRuleException, 409 to
// ApiConcurrencyException, everything else (500, the rare command-path 404, and any
// unrecognized status) to ApiInfrastructureException. The structured error body the
// Api host's ExceptionMappingMiddleware emits is read into ApiErrorBody and carried
// onto the thrown exception so the Web UI can render a category-specific message.
internal sealed class ApiClient : IApiClient
{
    private readonly HttpClient _httpClient;
    private readonly CommandTypeRegistry _commandRegistry;
    private readonly QueryTypeRegistry _queryRegistry;
    private readonly ICircuitForwardedIdentityProvider _identity;
    private readonly ForwardedIdentitySigner _signer;

    public ApiClient(
        HttpClient httpClient,
        CommandTypeRegistry commandRegistry,
        QueryTypeRegistry queryRegistry,
        ICircuitForwardedIdentityProvider identity,
        ForwardedIdentitySigner signer)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(commandRegistry);
        ArgumentNullException.ThrowIfNull(queryRegistry);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(signer);
        _httpClient = httpClient;
        _commandRegistry = commandRegistry;
        _queryRegistry = queryRegistry;
        _identity = identity;
        _signer = signer;
    }

    public async Task<CommandAcceptedResponse> SendCommandAsync(
        ICommand command,
        string idempotencyKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var typeName = _commandRegistry.NameFor(command.GetType());
        var content = JsonContent.Create(
            new { type = typeName, payload = command },
            options: JsonSerializerOptions.Web);
        var request = new HttpRequestMessage(HttpMethod.Post, "/commands")
        {
            Content = content,
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        await AttachForwardedIdentityAsync(request);
        var response = await _httpClient.SendAsync(request, ct);
        await ThrowMappedAsync(response, ct);
        var accepted = await response.Content.ReadFromJsonAsync<CommandAcceptedResponse>(
            JsonSerializerOptions.Web, ct);
        return accepted ?? throw new InvalidOperationException(
            "The /commands endpoint returned a null CommandAcceptedResponse body.");
    }

    public async Task<TResult?> QueryAsync<TResult>(
        IQuery<TResult> query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var typeName = _queryRegistry.NameFor(query.GetType());
        var content = JsonContent.Create(
            new { type = typeName, payload = query },
            options: JsonSerializerOptions.Web);
        var request = new HttpRequestMessage(HttpMethod.Post, "/queries")
        {
            Content = content,
        };
        await AttachForwardedIdentityAsync(request);
        var response = await _httpClient.SendAsync(request, ct);
        // The Api host's QueriesEndpoint returns 404 for a null query result (the
        // nullable single-row and composed views). Surface that as a default TResult?
        // here; list queries never reach this branch because they never return null.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }
        await ThrowMappedAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TResult>(
            JsonSerializerOptions.Web, ct);
    }

    // Attaches the circuit's signed forwarded identity: the actor id formatted with an empty role set
    // ("{actorId:N};") and its base64url HMAC-SHA256 signature, under the header names the Api verifier
    // reads (2a). The actor is read from the circuit-scoped provider at send time, and a circuit
    // without an identity throws fail-closed, so no unsigned request leaves the client. The roles are
    // empty by design: the Api host loads the actor's authoritative roles, never trusting the wire.
    private async Task AttachForwardedIdentityAsync(HttpRequestMessage request)
    {
        var actorId = await _identity.GetActorIdAsync();
        var identityValue = ForwardedIdentityValue.Format(actorId, Array.Empty<Role>());
        request.Headers.Add(ForwardedIdentityHeaders.HeaderName, identityValue);
        request.Headers.Add(ForwardedIdentityHeaders.SignatureHeaderName, _signer.Sign(identityValue));
    }

    // Maps a non-success response to the matching ApiClientException; returns without
    // throwing on a 2xx. The command-path 404 reaches here (the query path handles its
    // own 404 as a null result before calling this) and folds into infrastructure: a
    // 404 on dispatch is the rare race where the aggregate vanished between the page
    // render and the button click.
    private static async Task ThrowMappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var body = await TryParseErrorBodyAsync(response, ct);

        switch (status)
        {
            case 400:
                throw new ApiValidationException(
                    body?.Errors ?? new Dictionary<string, IReadOnlyList<string>>(),
                    body?.Code,
                    body?.Message);
            case 422:
                throw new ApiBusinessRuleException(
                    body?.Code ?? "BUSINESS_RULE_VIOLATION",
                    body?.Message ?? "The command violated a business rule.");
            case 409:
                throw new ApiConcurrencyException(body?.ExpectedVersion ?? -1);
            default:
                throw new ApiInfrastructureException(
                    body?.Message ?? $"Api request failed with status {status}.",
                    statusCode: status,
                    code: body?.Code);
        }
    }

    private static async Task<ApiErrorBody?> TryParseErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<ApiErrorBody>(
                stream, JsonSerializerOptions.Web, ct);
        }
        catch (JsonException)
        {
            // The response body was not JSON or did not match ApiErrorBody. Return
            // null; the caller substitutes a generic message.
            return null;
        }
    }
}
