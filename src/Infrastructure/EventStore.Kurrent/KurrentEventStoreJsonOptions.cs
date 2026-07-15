using System.Text.Encodings.Web;
using System.Text.Json;

namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

// The adapter's serialization seam, its own file per ADR 0004's self-contained rule. The shape is
// copied from the relational adapters so a payload serialized on any engine round-trips on any
// other: snake_case_lower naming, TenantIdJsonConverter for the flat tenant scalar, and the pinned
// JavaScriptEncoder.Default.
//
// The encoder is named rather than inherited, matching the other adapters. JavaScriptEncoder.Default
// escapes every non-ASCII character to \uXXXX, so a serialized payload is pure ASCII by the time it
// reaches the wire. That is already the framework default; naming it makes it a decision rather than
// a default that can be flipped without anyone noticing what it was holding up. It carries the
// contract suite's non-ASCII round-trip fact on this engine too.
internal static class KurrentEventStoreJsonOptions
{
    public static JsonSerializerOptions Create()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new TenantIdJsonConverter() },
            Encoder = JavaScriptEncoder.Default,
        };
}
