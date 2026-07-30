using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Versioning;

// Unit coverage for the tenant on EventMetadata: the flat-scalar wire shape the
// events table's generated tenant_id column depends on, and the tolerant read that
// maps a pre-tenancy row (absent key or explicit null) to the default tenant.
public class EventMetadataTenantTests
{
    private static readonly DateTime At = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    private static EventMetadata Metadata(TenantId tenant)
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            OccurredUtc: At,
            Tenant: tenant);

    [Fact]
    public void Tenant_serializes_as_a_flat_scalar_under_the_tenant_id_key()
    {
        var tenant = TenantId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var json = JsonSerializer.Serialize(Metadata(tenant), EventStoreJsonOptions.Create());

        // The generated column reads metadata->>'tenant_id' and casts ::uuid
        // (migration 0016), so the value must be a bare string, never a nested
        // { "value": ... } object.
        json.Should().Contain("\"tenant_id\":\"11111111-1111-1111-1111-111111111111\"");
        json.Should().NotContain("\"tenant_id\":{");
    }

    [Fact]
    public void Reading_metadata_with_no_tenant_id_key_yields_the_default_tenant()
    {
        var json =
            $"{{\"correlation_id\":\"{Guid.NewGuid()}\",\"causation_id\":\"{Guid.NewGuid()}\"}}";

        var metadata = EventMetadataReader.Read(json, EventStoreJsonOptions.Create());

        metadata.Tenant.Should().Be(WellKnownTenants.Default);
    }

    [Fact]
    public void Reading_metadata_with_a_null_tenant_id_yields_the_default_tenant()
    {
        var json =
            $"{{\"correlation_id\":\"{Guid.NewGuid()}\",\"tenant_id\":null}}";

        var metadata = EventMetadataReader.Read(json, EventStoreJsonOptions.Create());

        metadata.Tenant.Should().Be(WellKnownTenants.Default);
    }

    [Fact]
    public void A_non_default_tenant_round_trips_through_serialization()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var original = Metadata(tenant);

        var json = JsonSerializer.Serialize(original, EventStoreJsonOptions.Create());
        var read = EventMetadataReader.Read(json, EventStoreJsonOptions.Create());

        read.Tenant.Should().Be(tenant);
        read.EventId.Should().Be(original.EventId);
    }
}
