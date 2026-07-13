using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Single tolerant read for the events metadata column. A pre-tenancy row has no tenant: the
// tenant_id key is absent, or present and JSON-null. System.Text.Json fills the non-nullable
// Tenant parameter with null and never invokes the converter for an absent or null value, so
// the coalesce below maps either legacy shape to the default tenant. It is the metadata-side
// mirror of migration 0001's COALESCE on the tenant_id computed column.
//
// Duplicated from the PostgreSQL adapter per ADR 0004. Every metadata deserialize in this
// adapter routes through here so the legacy mapping lives in one place.
internal static class EventMetadataReader
{
    public static EventMetadata Read(string json, JsonSerializerOptions options)
    {
        var metadata = JsonSerializer.Deserialize<EventMetadata>(json, options)
            ?? throw new InvalidOperationException("Event metadata column deserialized to null.");

        // The member is declared non-nullable, but STJ leaves it null for a legacy row. Read it
        // through a nullable local so the null test stays honest under Nullable=enable and
        // TreatWarningsAsErrors, then coalesce.
        TenantId? tenant = metadata.Tenant;
        return tenant is null
            ? metadata with { Tenant = WellKnownTenants.Default }
            : metadata;
    }
}
