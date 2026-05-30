using System.Net.Http.Json;
using EventSourcingCqrs.Hosts.Api.Authentication;
using EventSourcingCqrs.IntegrationTests.Authentication;

namespace EventSourcingCqrs.IntegrationTests;

internal static class HttpClientQueryExtensions
{
    public static Task<HttpResponseMessage> PostQueryAsync(
        this HttpClient client,
        string type,
        object payload,
        CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/queries")
        {
            Content = JsonContent.Create(new { type, payload }),
        };
        // /queries is gated (Phase 9): send the default forwarded identity so the query tests stay
        // authenticated. The unauthenticated-path test builds its own request without it.
        request.Headers.Add(ForwardedIdentityDefaults.HeaderName, ForwardedIdentityTestHeader.Default);
        return client.SendAsync(request, ct);
    }
}
