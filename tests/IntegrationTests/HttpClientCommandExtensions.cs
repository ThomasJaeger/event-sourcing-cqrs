using System.Net.Http.Json;
using EventSourcingCqrs.Hosts.Api.Authentication;
using EventSourcingCqrs.IntegrationTests.Authentication;

namespace EventSourcingCqrs.IntegrationTests;

internal static class HttpClientCommandExtensions
{
    public static async Task<HttpResponseMessage> PostCommandAsync(
        this HttpClient client,
        string type,
        object payload,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var content = JsonContent.Create(new { type, payload });
        var request = new HttpRequestMessage(HttpMethod.Post, "/commands")
        {
            Content = content,
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        // /commands is gated (Phase 9): send the default forwarded identity and its signature so the
        // command tests stay authenticated (P9.3b). The unauthenticated-path test builds its own
        // request without these headers.
        request.Headers.Add(ForwardedIdentityDefaults.HeaderName, ForwardedIdentityTestHeader.Default);
        request.Headers.Add(
            ForwardedIdentityDefaults.SignatureHeaderName,
            ForwardedIdentityTestHeader.SignatureFor(ForwardedIdentityTestHeader.Default));
        return await client.SendAsync(request, ct);
    }
}
