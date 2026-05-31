using System.Net.Http.Json;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Domain.Abstractions;
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
        // /queries is gated (Phase 9): send the default forwarded identity and its signature so the
        // query tests stay authenticated (P9.3b). The unauthenticated-path test builds its own request
        // without these headers.
        request.Headers.Add(ForwardedIdentityHeaders.HeaderName, ForwardedIdentityTestHeader.Default);
        request.Headers.Add(
            ForwardedIdentityHeaders.SignatureHeaderName,
            ForwardedIdentityTestHeader.SignatureFor(ForwardedIdentityTestHeader.Default));
        return client.SendAsync(request, ct);
    }

    // Posts a query as a specific actor, signing a forwarded identity for it the way the Web host does:
    // the actor id with an empty role set, since the Api host loads the actor's authoritative roles from
    // the read model (a test seeds them with ApiFixture.SeedRoleAsync). Used by the authorization tests
    // that need a narrower-than-Admin principal.
    public static Task<HttpResponseMessage> PostQueryAsAsync(
        this HttpClient client,
        string type,
        object payload,
        Guid actorId,
        CancellationToken ct = default)
    {
        var identity = ForwardedIdentityValue.Format(actorId, Array.Empty<Role>());
        var request = new HttpRequestMessage(HttpMethod.Post, "/queries")
        {
            Content = JsonContent.Create(new { type, payload }),
        };
        request.Headers.Add(ForwardedIdentityHeaders.HeaderName, identity);
        request.Headers.Add(
            ForwardedIdentityHeaders.SignatureHeaderName,
            ForwardedIdentityTestHeader.SignatureFor(identity));
        return client.SendAsync(request, ct);
    }
}
