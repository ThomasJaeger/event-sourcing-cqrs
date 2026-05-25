using System.Net.Http.Json;

namespace EventSourcingCqrs.IntegrationTests;

internal static class HttpClientQueryExtensions
{
    public static Task<HttpResponseMessage> PostQueryAsync(
        this HttpClient client,
        string type,
        object payload,
        CancellationToken ct = default)
    {
        var content = JsonContent.Create(new { type, payload });
        return client.PostAsync("/queries", content, ct);
    }
}
