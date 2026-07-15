using System.Text.Json;
using System.Text.Json.Serialization;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

// Serializes TenantId as a flat scalar: the bare Guid as a JSON string, not a nested
// { "value": ... } object. Duplicated from the relational adapters per ADR 0004. On KurrentDB
// there is no PERSISTED computed column reading it, but the flat shape stays so metadata is
// byte-identical across engines and a tenant read can key on the same scalar (slice 4).
//
// TenantId stays attribute-free (ADR 0029); the serialization choice is an adapter concern and
// registers on the options.
internal sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    public override TenantId Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TenantId.From(reader.GetGuid());

    public override void Write(
        Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
