using System.Text.Json;
using System.Text.Json.Serialization;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.SignalR;

// Serializes TenantId as a flat scalar (the bare GUID as a JSON string) on the
// projection_committed wire, so the broadcast group can be built from a
// parseable tenant. This mirrors the flat shape EventStore.Postgres uses for
// the events-table metadata tenant; the two are kept as separate
// adapter-owned registrations because TenantId stays attribute-free (ADR 0029)
// and each adapter owns the options it serializes through. The shapes are
// pinned independently by each adapter's round-trip test.
internal sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    public override TenantId Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TenantId.From(reader.GetGuid());

    public override void Write(
        Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
