using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Versioning;

// Single tolerant read for the events and outbox metadata column. A pre-tenancy row has no
// tenant: the tenant_id key is absent, or present and JSON-null. System.Text.Json fills the
// non-nullable Tenant parameter with null and never invokes the converter for an absent or
// null value, so the coalesce below maps either legacy shape to the default tenant.
//
// Reading an old shape into the current type is the versioning concern this project owns, and
// it is the same read on every engine, so it is consolidated here from the two relational
// adapters per ADR 0048. It mirrors PostgreSQL's and SQL Server's migration 0001 COALESCE on
// the tenant_id computed column, and StreamId's two-segment Parse tolerance (ADR 0011). This
// is the coalesce Phase 15's upcaster pipeline generalizes: a hand-written tolerance for one
// field, which the pipeline replaces with a versioned chain.
//
// The KurrentDB adapter keeps its own coalesce rather than calling this one. It reads a
// different type through a different shape (StoredEventMetadata over ReadOnlyMemory<byte>,
// with the metadata nested a level down), so adopting this would change what it reads, not
// just where the code lives.
public static class EventMetadataReader
{
    public static EventMetadata Read(string json, JsonSerializerOptions options)
    {
        var metadata = JsonSerializer.Deserialize<EventMetadata>(json, options)
            ?? throw new InvalidOperationException(
                "Event metadata column deserialized to null.");

        // The member is declared non-nullable, but STJ leaves it null for a
        // legacy row. Read it through a nullable local so the null test stays
        // honest under Nullable=enable and TreatWarningsAsErrors, then coalesce.
        TenantId? tenant = metadata.Tenant;
        return tenant is null
            ? metadata with { Tenant = WellKnownTenants.Default }
            : metadata;
    }
}
