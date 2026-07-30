using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Kurrent;
using EventSourcingCqrs.Infrastructure.Versioning;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Versioning;

// Characterization of the shared options factory's unmapped-member handling: a stored document that
// carries a member the target type does not declare reads without fault, and every declared member
// still binds. Green on write. The behavior predates the setting that now states it, because
// System.Text.Json skips unmapped members by default and this repository had never said so, so these
// facts describe what already held rather than driving new code. They exist so that narrowing the
// setting later fails here instead of quietly changing what a stored document is allowed to contain.
//
// The probe member is synthetic, and the first fact is what keeps it so. EventMetadata gained its
// tenant at dd52e92 and has never lost a member, so every shape the store has written is a subset of
// the current one and a key absent from a fully populated current document is absent from all of
// them. The schema-version member would prove nothing here: the type still declares it, so a reader
// binds it rather than skipping it.
//
// The options come from EventStoreJsonOptions.Create() and never from a locally built object. A local
// object pins the test's own configuration instead of the one the four adapters resolve, which is the
// drift the shared seam exists to prevent (ADR 0048).
public class EventStoreJsonOptionsTests
{
    private const string ProbeMember = "unmapped_probe";
    private const string ProbeValue = "a value no declared member claims";

    private static readonly DateTime At = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    private static EventMetadata SampleMetadata()
        => new(
            EventId: Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            CorrelationId: Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
            CausationId: Guid.Parse("cccccccc-0000-0000-0000-000000000003"),
            ActorId: Guid.Parse("dddddddd-0000-0000-0000-000000000004"),
            Source: "characterization",
            SchemaVersion: 1,
            OccurredUtc: At,
            Tenant: TenantId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000000005")));

    // The guard against a vacuous characterization. Were the probe name ever to become a declared
    // member, the two facts below would pin binding rather than skipping and would still pass.
    // Serializing a fully populated instance covers both naming routes at once, the snake-case policy
    // and the explicit property name the tenant carries.
    [Fact]
    public void The_probe_member_is_one_the_metadata_type_does_not_declare()
    {
        var options = EventStoreJsonOptions.Create();

        var document = JsonNode.Parse(JsonSerializer.Serialize(SampleMetadata(), options))!.AsObject();

        document.ContainsKey(ProbeMember).Should().BeFalse();
    }

    [Fact]
    public void A_document_carrying_an_undeclared_member_reads_with_every_declared_member_bound()
    {
        var options = EventStoreJsonOptions.Create();
        var original = SampleMetadata();
        var document = JsonNode.Parse(JsonSerializer.Serialize(original, options))!.AsObject();
        document[ProbeMember] = ProbeValue;

        var read = EventMetadataReader.Read(document.ToJsonString(options), options);

        // EventMetadata is a record, so one equality covers all eight declared members.
        read.Should().Be(original);
    }

    // The KurrentDB adapter reads a different type through a different call than the relational and
    // DynamoDB adapters do: StoredEventMetadata over bytes rather than EventMetadata over a string,
    // in KurrentEventHydration.ReadStoredMetadata, with its own tenant coalesce instead of the shared
    // reader's. The options factory is the only thing the two paths share, so the nested slot needs
    // its own fact. The byte overload is the one the adapter calls, so this calls it too.
    [Fact]
    public void An_undeclared_member_nested_in_the_kurrent_metadata_slot_reads_with_the_slot_bound()
    {
        var options = EventStoreJsonOptions.Create();
        var original = new StoredEventMetadata(
            EventVersion: 2, OccurredUtc: At, Metadata: SampleMetadata());
        var document = JsonNode.Parse(JsonSerializer.Serialize(original, options))!.AsObject();
        document["metadata"]!.AsObject()[ProbeMember] = ProbeValue;

        var read = JsonSerializer.Deserialize<StoredEventMetadata>(
            Encoding.UTF8.GetBytes(document.ToJsonString(options)), options);

        read.Should().Be(original);
    }
}
