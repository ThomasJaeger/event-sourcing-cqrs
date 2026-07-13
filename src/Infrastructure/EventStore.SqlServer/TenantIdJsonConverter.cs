using System.Text.Json;
using System.Text.Json.Serialization;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Serializes TenantId as a flat scalar: the bare Guid as a JSON string, not a nested
// { "value": ... } object. The flat shape is load-bearing here for the same reason it is on
// PostgreSQL: migration 0001's PERSISTED computed column reads JSON_VALUE(metadata,
// '$.tenant_id') and converts to UNIQUEIDENTIFIER, which parses only if the tenant sits as a
// scalar at that key.
//
// Duplicated from the PostgreSQL adapter per ADR 0004. TenantId stays attribute-free (ADR 0029);
// the serialization choice is an adapter concern and registers on the options.
internal sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    public override TenantId Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TenantId.From(reader.GetGuid());

    public override void Write(
        Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
