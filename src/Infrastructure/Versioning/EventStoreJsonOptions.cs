using System.Text.Encodings.Web;
using System.Text.Json;

namespace EventSourcingCqrs.Infrastructure.Versioning;

// The event store's serialization seam. One factory for every engine, consolidated per ADR
// 0048 from two inline private copies in the relational composition roots and one dedicated
// file in the KurrentDB adapter. This shape is the reason the seam is shared: a payload
// serialized on any engine round-trips on any other only while all of them agree on naming,
// converters, and escaping. Three copies is how an options pin gets applied to two of them
// and missed on the third.
//
// snake_case_lower so payload and metadata round-trip through the relational schemas'
// generated columns. The options freeze on first use, so a host wanting different ones
// pre-registers its own.
//
// Encoder is pinned rather than inherited. JavaScriptEncoder.Default escapes every non-ASCII
// character to \uXXXX, so a serialized payload is pure ASCII on the wire and in the column.
// That is already the framework default; naming it makes it a decision the next person has to
// argue with rather than a default they can flip without noticing.
//
// What the pin protects: relaxing to UnsafeRelaxedJsonEscaping sends raw UTF-8 bytes to the
// driver, and from that moment every adapter's parameter binding has to be byte-correct or it
// corrupts data silently. The SQL Server adapter is byte-correct and SqlServerOutboxEncodingTests
// proves it on the raw stored bytes, which the engine-agnostic contract suite cannot: its
// payloads are ASCII by the time they leave the serializer. The pin keeps the escaping a
// deliberate choice on every engine rather than the thing quietly holding the roof up.
public static class EventStoreJsonOptions
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
