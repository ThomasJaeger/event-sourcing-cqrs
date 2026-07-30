using System.Text.Json;
using System.Text.Json.Nodes;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using FG = FsCheck.Fluent.Gen;
using FP = FsCheck.Fluent.Prop;

namespace EventSourcingCqrs.PropertyTests;

// Property: any well-formed EventMetadata round-trips through EventStoreJsonOptions.Create(). This pins
// the shared-options seam (ADR 0048) as a property over the whole metadata field space rather than one
// example: the RED that proved the round-trip mechanism is the example-based EventMetadataTenantTests,
// and this generalizes it. The generators cover the traps the seam exists to catch: non-ASCII and
// special characters in the source string, boundary UTC DateTimes, and both tenant cases, a present
// tenant that round-trips as itself and an absent one that coalesces to the default on the read path.
public class MetadataRoundTripProperties
{
    // The pieces a source string is built from: ASCII, accented Latin, CJK, emoji (well-formed surrogate
    // pairs), quotes, backslashes, and control characters. Concatenating well-formed units keeps every
    // generated string valid UTF-16, so no lone surrogate trips the encoder; the escaping the seam pins
    // is exercised by the non-ASCII and control characters, not by malformed input.
    private static readonly string[] SourceParts =
        ["a", "Z", "0", "_", " ", "é", "ñ", "ü", "中", "日", "😀", "🎉", "\"", "\\", "/", "\n", "\t", "\r", ":", "{", "}"];

    private static readonly Gen<Guid> AnyGuid =
        FG.Choose(int.MinValue, int.MaxValue).Select(i => new Guid(i, 0, 0, new byte[8]));

    // Never Guid.Empty: TenantId.From rejects it, and a tenant is the one metadata field with validation.
    private static readonly Gen<Guid> NonEmptyGuid =
        FG.Choose(1, int.MaxValue).Select(i => new Guid(i, 0, 0, new byte[8]));

    private static readonly Gen<string> AnySource =
        FG.ListOf(FG.Elements(SourceParts)).Select(parts => string.Concat(parts));

    private static readonly Gen<DateTime> RandomUtcDateTime =
        from year in FG.Choose(1, 9999)
        from month in FG.Choose(1, 12)
        from day in FG.Choose(1, 28)
        from hour in FG.Choose(0, 23)
        from minute in FG.Choose(0, 59)
        from second in FG.Choose(0, 59)
        from ticks in FG.Choose(0, 9_999_999)
        select new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddTicks(ticks);

    // UTC kind throughout: System.Text.Json round-trips a UTC DateTime to an equal one, but a Local or
    // Unspecified kind shifts under a non-zero machine offset. The boundary values (min, max, epoch) sit
    // beside a spread of random instants with sub-second ticks. Declared after RandomUtcDateTime so the
    // static initializer captures a live generator, not a null slot.
    private static readonly Gen<DateTime> AnyUtcDateTime =
        FG.Frequency(
            (1, FG.Elements(
                DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
                DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
                DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc))),
            (4, RandomUtcDateTime));

    private static readonly Gen<(EventMetadata Metadata, bool TenantPresent)> Metadata =
        from eventId in AnyGuid
        from correlationId in AnyGuid
        from causationId in AnyGuid
        from actorId in AnyGuid
        from source in AnySource
        from occurredUtc in AnyUtcDateTime
        from tenantGuid in NonEmptyGuid
        from tenantPresent in FG.Elements(true, false)
        select (
            new EventMetadata(
                eventId,
                correlationId,
                causationId,
                actorId,
                source,
                occurredUtc,
                tenantPresent ? TenantId.From(tenantGuid) : WellKnownTenants.Default),
            tenantPresent);

    // MaxTest 300: the field space is wide (four Guids, an unbounded source string, the full UTC
    // DateTime range, and the tenant branch), so a larger sample than the default 100. Replay is fixed
    // so a falsification reproduces from the same seed every run rather than a fresh one.
    [Property(MaxTest = 300, Replay = "20260721,90909091")]
    public FsCheck.Property EventMetadata_round_trips_through_the_shared_serializer_options()
        => FP.ForAll(Metadata.ToArbitrary(), testCase =>
        {
            var (metadata, tenantPresent) = testCase;
            var options = EventStoreJsonOptions.Create();

            var json = JsonSerializer.Serialize(metadata, options);
            if (!tenantPresent)
            {
                // A legacy row written before tenancy carries no tenant_id; drop it so the read path's
                // coalesce to the default tenant is what the round-trip has to land on.
                json = WithoutTenantId(json, options);
            }

            var read = EventMetadataReader.Read(json, options);

            return read.Equals(metadata);
        });

    private static string WithoutTenantId(string json, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("tenant_id");
        return node.ToJsonString(options);
    }
}
