using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public sealed class PostgresAuditEventReaderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Correlation = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly TenantId Tenant = WellKnownTenants.Default;
    private static readonly DateTime Recorded = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private const string Stream = "order:33333333333333333333333333333333";

    [Fact]
    public async Task Descending_pages_retain_the_captured_upper_position_and_refresh_sees_new_events()
    {
        await using var source = await SourceAsync();
        var positions = new List<long>();
        for (var version = 1; version <= 5; version++)
            positions.Add(await SeedAsync(source, Stream, version, occurred: Recorded.AddDays(-version)));
        var reader = Reader(source);
        var first = await reader.ReadPageAsync(new(), null, null, 2, CancellationToken.None);
        var newest = await SeedAsync(source, Stream, 6);
        var second = await reader.ReadPageAsync(new(), first.UpperPosition, first.NextBeforePosition, 2, CancellationToken.None);
        var last = await reader.ReadPageAsync(new(), first.UpperPosition, second.NextBeforePosition, 2, CancellationToken.None);
        var refreshed = await reader.ReadPageAsync(new(), null, null, 2, CancellationToken.None);

        first.Items.Select(x => x.GlobalPosition).Should().Equal(positions[4], positions[3]);
        first.UpperPosition.Should().Be(positions[4]);
        first.NextBeforePosition.Should().Be(positions[3]);
        second.Items.Select(x => x.GlobalPosition).Should().Equal(positions[2], positions[1]);
        second.UpperPosition.Should().Be(first.UpperPosition);
        last.Items.Select(x => x.GlobalPosition).Should().Equal(positions[0]);
        last.NextBeforePosition.Should().BeNull();
        refreshed.Items[0].GlobalPosition.Should().Be(newest);
        refreshed.UpperPosition.Should().Be(newest);
    }

    [Fact]
    public async Task An_empty_store_and_an_empty_filtered_page_still_report_the_boundary()
    {
        await using var source = await SourceAsync();
        var reader = Reader(source);
        var empty = await reader.ReadPageAsync(new(), null, null, 50, CancellationToken.None);
        empty.Should().BeEquivalentTo(new AuditEventPage([], 0, null));
        var position = await SeedAsync(source, Stream, 1);
        var filtered = await reader.ReadPageAsync(new(ActorId: Guid.NewGuid()), null, null, 50, CancellationToken.None);
        filtered.Items.Should().BeEmpty();
        filtered.UpperPosition.Should().Be(position);
        filtered.NextBeforePosition.Should().BeNull();
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("correlation")]
    [InlineData("tenant")]
    [InlineData("actor")]
    public async Task Each_optional_filter_is_exact_and_does_not_hide_other_tenants_by_default(string field)
    {
        await using var source = await SourceAsync();
        var wanted = await SeedAsync(source, Stream, 1);
        var otherStream = "payment:44444444444444444444444444444444";
        await SeedAsync(source, field == "stream" ? otherStream : Stream, 2,
            correlation: field == "correlation" ? Guid.NewGuid() : Correlation,
            tenant: field == "tenant" ? TenantId.From(Guid.NewGuid()) : Tenant,
            actor: field == "actor" ? Guid.NewGuid() : Actor);
        var filter = field switch
        {
            "stream" => new AuditEventFilter(StreamId: Stream),
            "correlation" => new AuditEventFilter(CorrelationId: Correlation),
            "tenant" => new AuditEventFilter(Tenant: Tenant),
            _ => new AuditEventFilter(ActorId: Actor),
        };
        var reader = Reader(source);
        (await reader.ReadPageAsync(new(), null, null, 50, CancellationToken.None)).Items.Should().HaveCount(2);
        var page = await reader.ReadPageAsync(filter, null, null, 50, CancellationToken.None);
        page.Items.Should().ContainSingle().Which.GlobalPosition.Should().Be(wanted);
    }

    [Fact]
    public async Task Combined_filters_are_intersected_and_raw_unknown_process_manager_events_remain_readable()
    {
        await using var source = await SourceAsync();
        const string pm = "pm-order-fulfillment:55555555555555555555555555555555";
        var position = await SeedAsync(source, pm, 1, actor: Guid.Empty);
        await SeedAsync(source, pm, 2);
        var page = await Reader(source).ReadPageAsync(new(pm, Correlation, Tenant, Guid.Empty), null, null, 50, CancellationToken.None);
        var row = page.Items.Should().ContainSingle().Subject;
        row.GlobalPosition.Should().Be(position);
        row.StreamId.Should().Be(pm);
        row.EventType.Should().Be("retired.UnknownAuditEvent");
        row.EventVersion.Should().Be(7);
        row.StreamVersion.Should().Be(1);
        row.Metadata.ActorId.Should().BeEmpty();
        row.Metadata.CorrelationId.Should().Be(Correlation);
        row.Metadata.Tenant.Should().Be(Tenant);
        row.OccurredUtc.Should().Be(Recorded);
        row.OccurredUtc.Kind.Should().Be(DateTimeKind.Utc);
        using var payload = JsonDocument.Parse(row.PayloadJson);
        payload.RootElement.GetProperty("recorded_value").GetString().Should().Be("original");
        using var metadata = JsonDocument.Parse(row.MetadataJson);
        metadata.RootElement.GetProperty("extra_audit_field").GetString().Should().Be("preserved");
        metadata.RootElement.GetProperty("event_id").GetGuid().Should().Be(row.EventId);
    }

    [Fact]
    public async Task Empty_actor_filter_also_finds_legacy_metadata_without_an_actor()
    {
        await using var source = await SourceAsync();
        var expected = await SeedAsync(source, Stream, 1, omitActor: true);
        await SeedAsync(source, Stream, 2);
        var page = await Reader(source).ReadPageAsync(new(ActorId: Guid.Empty), null, null, 50, CancellationToken.None);
        page.Items.Should().ContainSingle().Which.GlobalPosition.Should().Be(expected);
        page.Items[0].Metadata.ActorId.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(int.MaxValue)]
    public async Task Page_sizes_outside_one_to_fifty_are_rejected(int size)
    {
        await using var source = await SourceAsync();
        await FluentActions.Awaiting(() => Reader(source).ReadPageAsync(new(), null, null, size, CancellationToken.None))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1L, null)]
    [InlineData(10L, 0L)]
    [InlineData(10L, 11L)]
    [InlineData(null, 5L)]
    public async Task Invalid_boundaries_and_a_cursor_without_its_upper_position_are_rejected(long? upper, long? before)
    {
        await using var source = await SourceAsync();
        await FluentActions.Awaiting(() => Reader(source).ReadPageAsync(new(), upper, before, 50, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Invalid_stream_input_and_cancellation_do_not_become_a_full_unfiltered_read()
    {
        await using var source = await SourceAsync();
        var reader = Reader(source);
        await FluentActions.Awaiting(() => reader.ReadPageAsync(new(StreamId: "' OR 1=1 --"), null, null, 50, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await FluentActions.Awaiting(() => reader.ReadPageAsync(new(), null, null, 50, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<NpgsqlDataSource> SourceAsync()
        => NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
    private static PostgresAuditEventReader Reader(NpgsqlDataSource source)
        => new(new NpgsqlConnectionFactory(source), EventStoreJsonOptions.Create());

    private static async Task<long> SeedAsync(NpgsqlDataSource source, string stream, int version,
        Guid? correlation = null, TenantId? tenant = null, Guid? actor = null,
        DateTime? occurred = null, bool omitActor = false)
    {
        var eventId = Guid.NewGuid();
        var metadata = new Dictionary<string, object?>
        {
            ["event_id"] = eventId, ["correlation_id"] = correlation ?? Correlation,
            ["causation_id"] = Guid.NewGuid(), ["tenant_id"] = (tenant ?? Tenant).Value,
            ["source"] = "audit-fixture", ["occurred_utc"] = occurred ?? Recorded,
            ["extra_audit_field"] = "preserved",
        };
        if (!omitActor) metadata["actor_id"] = actor ?? Actor;
        await using var command = source.CreateCommand("""
            INSERT INTO event_store.events
                (stream_id, stream_version, event_id, event_type, event_version, payload, metadata, occurred_utc)
            VALUES (@stream, @version, @event, 'retired.UnknownAuditEvent', 7, @payload, @metadata, @occurred)
            RETURNING global_position
            """);
        command.Parameters.AddWithValue("stream", stream);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("event", eventId);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, "{\"recorded_value\":\"original\"}");
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(metadata));
        command.Parameters.AddWithValue("occurred", occurred ?? Recorded);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
