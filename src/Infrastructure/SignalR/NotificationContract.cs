using System.Text.Json;

namespace EventSourcingCqrs.Infrastructure.SignalR;

// The single source of truth for the two facts the pg_notify publisher and the
// hub backplane must agree on across hosts: the LISTEN/NOTIFY channel and the
// envelope's JSON casing. The two run in different processes, so the agreement
// lives in shared code the compiler carries to both sides rather than in two
// literals that happen to match. A casing mismatch is otherwise silent: the
// payload deserializes to a record with null routing fields, clears the
// backplane's not-null and not-malformed guards, and misroutes with no error.
public static class NotificationContract
{
    // The channel PostgresPgNotifyPublisher issues NOTIFY on and the hub
    // backplane LISTENs on.
    public const string ChannelName = "projection_committed";

    // snake_case, the same shape as the event-store serializer. Freezes on
    // first use like any JsonSerializerOptions; treat it as immutable.
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
