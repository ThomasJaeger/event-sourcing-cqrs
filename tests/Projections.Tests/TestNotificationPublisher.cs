using System.Text.Json;
using EventSourcingCqrs.Infrastructure.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventSourcingCqrs.Projections.Tests;

// A PostgresPgNotifyPublisher for the Postgres store tests. Most store tests
// never stage a notification, so the publisher is inert there; the publication
// tests in PostgresOrderListStoreTests use the snake_case options so a LISTEN
// subscriber deserializes the envelope the same way the production hub will.
internal static class TestNotificationPublisher
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static PostgresPgNotifyPublisher Create()
        => new(JsonOptions, NullLogger<PostgresPgNotifyPublisher>.Instance);
}
