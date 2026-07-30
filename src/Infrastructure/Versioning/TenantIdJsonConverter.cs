using System.Text.Json;
using System.Text.Json.Serialization;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Versioning;

// Serializes TenantId as a flat scalar: the bare Guid as a JSON string, not a nested
// { "value": ... } object. The flat shape is load-bearing on both relational engines.
// PostgreSQL's STORED generated column reads metadata->>'tenant_id' and casts ::uuid;
// SQL Server migration 0001's PERSISTED computed column reads JSON_VALUE(metadata,
// '$.tenant_id') and converts to UNIQUEIDENTIFIER. Both parse only if the tenant sits as
// a scalar at that key. KurrentDB has no computed column reading it and keeps the shape
// anyway, so metadata stays byte-identical across engines.
//
// TenantId stays attribute-free (ADR 0029), so the choice lives on the options rather
// than on the domain type. Consolidated here from the three adapters per ADR 0048; that
// cross-engine byte-identity is what makes this a versioning concern rather than an
// engine one. The SignalR notification wire keeps its own converter: it serializes a
// different contract and pins its shape independently.
//
// Internal, as it was in every adapter that carried it. No adapter names this type: they
// reach the converter through EventStoreJsonOptions.Create(), which is the seam. The one
// test grant in the csproj exists because the outbox encoding fixture constructs it
// directly, to assemble the stored shape with the encoder relaxed.
internal sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    public override TenantId Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TenantId.From(reader.GetGuid());

    public override void Write(
        Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
