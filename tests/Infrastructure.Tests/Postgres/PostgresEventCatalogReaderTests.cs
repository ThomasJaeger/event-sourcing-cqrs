using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public sealed class PostgresEventCatalogReaderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateTime Earlier = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Earlier.AddHours(1);
    private static readonly Guid TenantA = WellKnownTenants.Default.Value;
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Stream_pages_are_distinct_in_id_order_and_resume_after_the_last_choice()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var expected = Enumerable.Range(1, 5).Select(Stream).ToArray();
        foreach (var stream in expected.Reverse())
        {
            await SeedAsync(source, stream, 1, Id(1), TenantA, Earlier);
            await SeedAsync(source, stream, 2, Id(1), TenantA, Later);
        }
        var reader = Reader(source);

        var first = await reader.ListStreamsAsync(null, 2, CancellationToken.None);
        var second = await reader.ListStreamsAsync(first.Items[^1].StreamId, 2, CancellationToken.None);
        var last = await reader.ListStreamsAsync(second.Items[^1].StreamId, 2, CancellationToken.None);
        var end = await reader.ListStreamsAsync(last.Items[^1].StreamId, 2, CancellationToken.None);

        first.Items.Select(x => x.StreamId).Should().Equal(expected.Take(2));
        second.Items.Select(x => x.StreamId).Should().Equal(expected.Skip(2).Take(2));
        last.Items.Select(x => x.StreamId).Should().Equal(expected.Skip(4));
        first.HasMore.Should().BeTrue();
        second.HasMore.Should().BeTrue();
        last.HasMore.Should().BeFalse();
        end.Items.Should().BeEmpty();
        end.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Correlation_pages_are_distinct_in_id_order_and_report_an_exactly_full_last_page()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        foreach (var i in Enumerable.Range(1, 4).Reverse())
        {
            await SeedAsync(source, Stream(i), 1, Id(i), TenantA, Earlier);
            await SeedAsync(source, Stream(i), 2, Id(i), TenantA, Later);
        }
        var reader = Reader(source);

        var first = await reader.ListCorrelationsAsync(null, 2, CancellationToken.None);
        var last = await reader.ListCorrelationsAsync(first.Items[^1].CorrelationId, 2, CancellationToken.None);
        var end = await reader.ListCorrelationsAsync(last.Items[^1].CorrelationId, 2, CancellationToken.None);

        first.Items.Select(x => x.CorrelationId).Should().Equal(Id(1), Id(2));
        last.Items.Select(x => x.CorrelationId).Should().Equal(Id(3), Id(4));
        first.HasMore.Should().BeTrue();
        last.HasMore.Should().BeFalse();
        end.Items.Should().BeEmpty();
        end.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Stream_choices_exclude_process_managers_but_their_correlations_remain_discoverable()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var pm = $"pm-order-fulfillment:{Id(2):N}";
        await SeedAsync(source, Stream(1), 1, Id(1), TenantA, Earlier);
        await SeedAsync(source, pm, 1, Id(2), TenantA, Earlier);
        var reader = Reader(source);

        var streams = await reader.ListStreamsAsync(null, 25, CancellationToken.None);
        var correlations = await reader.ListCorrelationsAsync(null, 25, CancellationToken.None);

        streams.Items.Select(x => x.StreamId).Should().Equal(Stream(1));
        correlations.Items.Select(x => x.CorrelationId).Should().Equal(Id(1), Id(2));
    }

    [Fact]
    public async Task Correlation_choices_exclude_missing_and_empty_ids_before_applying_the_page_limit()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        await SeedAsync(source, Stream(1), 1, null, TenantA, Earlier);
        await SeedAsync(source, Stream(1), 2, Guid.Empty, TenantA, Earlier);
        await SeedAsync(source, Stream(1), 3, Id(1), TenantA, Earlier);

        var page = await Reader(source).ListCorrelationsAsync(null, 1, CancellationToken.None);

        page.Items.Select(x => x.CorrelationId).Should().Equal(Id(1));
        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Stream_summaries_count_all_tenants_and_use_event_time_instead_of_append_order()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        await SeedAsync(source, Stream(1), 1, Id(1), TenantA, Later);
        await SeedAsync(source, Stream(1), 2, Id(2), TenantB, Earlier);
        await SeedAsync(source, Stream(1), 3, Id(3), TenantB, Earlier);
        await SeedAsync(source, Stream(2), 1, Id(4), TenantA, Later.AddDays(1));

        var page = await Reader(source).ListStreamsAsync(null, 1, CancellationToken.None);

        page.Items.Should().ContainSingle().Which.Should().Be(
            new StreamCatalogEntry(Stream(1), 3, Later, 2));
        page.Items[0].LastOccurredUtc.Kind.Should().Be(DateTimeKind.Utc);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Correlation_summaries_count_events_distinct_streams_and_tenants_including_process_managers()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        await SeedAsync(source, Stream(1), 1, Id(1), TenantA, Later);
        await SeedAsync(source, Stream(1), 2, Id(1), TenantA, Earlier);
        await SeedAsync(source, Stream(2), 1, Id(1), TenantB, Earlier);
        await SeedAsync(source, $"pm-return:{Id(3):N}", 1, Id(1), TenantB, Earlier);
        await SeedAsync(source, Stream(3), 1, Id(2), TenantA, Later.AddDays(1));

        var page = await Reader(source).ListCorrelationsAsync(null, 1, CancellationToken.None);

        page.Items.Should().ContainSingle().Which.Should().Be(
            new CorrelationCatalogEntry(Id(1), 4, 3, 2, Later));
        page.Items[0].LastOccurredUtc.Kind.Should().Be(DateTimeKind.Utc);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Catalogs_do_not_require_payload_types_or_valid_domain_payloads()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        // This retired event type has no CLR registration and its JSON string is not a domain record.
        await SeedAsync(source, Stream(1), 1, Id(1), TenantA, Later);
        var reader = Reader(source);

        var streams = await reader.ListStreamsAsync(null, 100, CancellationToken.None);
        var correlations = await reader.ListCorrelationsAsync(null, 100, CancellationToken.None);

        streams.Items.Should().ContainSingle().Which.EventCount.Should().Be(1);
        correlations.Items.Should().ContainSingle().Which.EventCount.Should().Be(1);
        streams.HasMore.Should().BeFalse();
        correlations.HasMore.Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task Page_sizes_outside_one_to_one_hundred_are_rejected(int size)
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var reader = Reader(source);
        await FluentActions.Awaiting(() => reader.ListStreamsAsync(null, size, CancellationToken.None))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => reader.ListCorrelationsAsync(null, size, CancellationToken.None))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("order:not-a-guid")]
    [InlineData("'; DROP TABLE event_store.events; --")]
    public async Task Malformed_stream_cursors_are_rejected(string cursor)
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        await FluentActions.Awaiting(() => Reader(source).ListStreamsAsync(cursor, 25, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Empty_correlation_cursors_are_rejected()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        await FluentActions.Awaiting(() => Reader(source).ListCorrelationsAsync(Guid.Empty, 25, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Cancellation_is_observed_by_both_catalog_reads()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var reader = Reader(source);
        await FluentActions.Awaiting(() => reader.ListStreamsAsync(null, 25, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => reader.ListCorrelationsAsync(null, 25, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    private static PostgresEventCatalogReader Reader(NpgsqlDataSource source)
        => new(new NpgsqlConnectionFactory(source));

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");
    private static string Stream(int value) => $"order:{Id(value):N}";

    private static async Task SeedAsync(
        NpgsqlDataSource source, string stream, int version, Guid? correlation, Guid tenant, DateTime occurred)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO event_store.events
                (stream_id, stream_version, event_id, event_type, event_version, payload, metadata, occurred_utc)
            VALUES (@stream, @version, @event_id, 'retired.UnknownEvent', 1, @payload, @metadata, @occurred)
            """);
        command.Parameters.AddWithValue("stream", stream);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("event_id", Guid.NewGuid());
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, "\"opaque future payload\"");
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(new { correlation_id = correlation, tenant_id = tenant }));
        command.Parameters.AddWithValue("occurred", occurred);
        await command.ExecuteNonQueryAsync();
    }
}
