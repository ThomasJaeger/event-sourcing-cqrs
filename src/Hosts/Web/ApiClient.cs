using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.Web;

// IApiClient's HTTP implementation. Builds the type-discriminator envelope from
// the registries (D3 shape: { type, payload }), serializes the payload against
// JsonSerializerOptions.Web (matching the Api host's Microsoft.AspNetCore.Http.Json
// default), sets Idempotency-Key on the command path. Deliberately symmetric with
// the test-only HttpClientCommandExtensions/HttpClientQueryExtensions in
// IntegrationTests; the test helpers prove the wire format, this production type
// consumes it with DI and lifetime management.
internal sealed class ApiClient : IApiClient
{
    private readonly HttpClient _httpClient;
    private readonly CommandTypeRegistry _commandRegistry;
    private readonly QueryTypeRegistry _queryRegistry;

    public ApiClient(
        HttpClient httpClient,
        CommandTypeRegistry commandRegistry,
        QueryTypeRegistry queryRegistry)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(commandRegistry);
        ArgumentNullException.ThrowIfNull(queryRegistry);
        _httpClient = httpClient;
        _commandRegistry = commandRegistry;
        _queryRegistry = queryRegistry;
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
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
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
        var response = await _httpClient.PostAsync("/queries", content, ct);
        // The Api host's QueriesEndpoint returns 404 for a null query result (the
        // nullable single-row and composed views). Surface that as a default TResult?
        // here; list queries never reach this branch because they never return null.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResult>(
            JsonSerializerOptions.Web, ct);
    }
}
