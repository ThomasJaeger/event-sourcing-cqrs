using System.Text.Json;
using System.Text.Json.Serialization;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Serializes TenantId as a flat scalar: the bare Guid as a JSON string, not a
// nested { "value": ... } object. The flat shape is load-bearing. A later
// migration adds a STORED generated column reading metadata->>'tenant_id' and
// casting ::uuid, which parses only if the tenant sits as a scalar at that key.
// TenantId stays attribute-free (a clean domain type, ADR 0029); this
// serialization choice is an adapter concern and registers on the options.
internal sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    public override TenantId Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TenantId.From(reader.GetGuid());

    public override void Write(
        Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
