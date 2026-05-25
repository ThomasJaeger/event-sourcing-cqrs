using System.Net.Http.Json;

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
        return await client.SendAsync(request, ct);
    }
}
