using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public sealed class PostgresBoundedEventStreamReaderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Guid OrderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CustomerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime At = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly StreamId Stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, OrderId);

    [Fact]
    public async Task The_bound_is_applied_before_hydrating_the_extra_row()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        await SeedAsync(source, Stream, 1, nameof(OrderDrafted), 2,
            JsonSerializer.Serialize(new OrderDrafted(OrderId, CustomerId, At, "web"), EventStoreJsonOptions.Create()));
        await SeedAsync(source, Stream, 2, "NoClrTypeExistsForThisEvent", 1, "\"opaque\"");

        var page = await Reader(source).ReadAsync(Stream, 1, CancellationToken.None);

        page.Events.Should().ContainSingle().Which.Payload.Should().BeOfType<OrderDrafted>();
        page.Events[0].StreamVersion.Should().Be(1);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task An_exactly_full_stream_is_complete_and_legacy_events_are_upcast()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var legacy = JsonSerializer.Serialize(new { order_id = OrderId, customer_id = CustomerId, drafted_utc = At });
        await SeedAsync(source, Stream, 1, nameof(OrderDrafted), 1, legacy);
        var before = await CountsAsync(source);

        var page = await Reader(source).ReadAsync(Stream, 1, CancellationToken.None);

        page.HasMore.Should().BeFalse();
        var envelope = page.Events.Should().ContainSingle().Subject;
        var drafted = envelope.Payload.Should().BeOfType<OrderDrafted>().Subject;
        drafted.OrderId.Should().Be(OrderId);
        drafted.CustomerId.Should().Be(CustomerId);
        drafted.Channel.Should().Be("unknown");
        envelope.EventVersion.Should().Be(2);
        envelope.OccurredUtc.Kind.Should().Be(DateTimeKind.Utc);
        envelope.Metadata.Tenant.Should().Be(WellKnownTenants.Default);
        (await CountsAsync(source)).Should().Equal(before);
    }

    [Fact]
    public async Task Versions_are_ordered_and_other_streams_and_tenants_are_excluded()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var payload = JsonSerializer.Serialize(new OrderDrafted(OrderId, CustomerId, At, "web"), EventStoreJsonOptions.Create());
        await SeedAsync(source, Stream, 2, nameof(OrderDrafted), 2, payload);
        var otherTenantStream = StreamId.ForAggregate<Order>(TenantId.From(Guid.NewGuid()), OrderId);
        await SeedAsync(source, otherTenantStream, 1, nameof(OrderDrafted), 2, payload);
        await SeedAsync(source, Stream, 1, nameof(OrderDrafted), 2, payload);

        var page = await Reader(source).ReadAsync(Stream, 500, CancellationToken.None);

        page.Events.Select(e => e.StreamVersion).Should().Equal(1, 2);
        page.Events.Should().OnlyContain(e => e.StreamId == Stream);
        page.HasMore.Should().BeFalse();
    }

    [Theory]
    [InlineData("unknown-type")]
    [InlineData("unknown-schema")]
    [InlineData("null-payload")]
    [InlineData("null-metadata")]
    public async Task Uninterpretable_rows_have_a_named_read_failure(string fault)
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var payload = fault == "null-payload" ? "null" : JsonSerializer.Serialize(
            new OrderDrafted(OrderId, CustomerId, At, "web"), EventStoreJsonOptions.Create());
        await SeedAsync(source, Stream, 1, fault == "unknown-type" ? "Unknown" : nameof(OrderDrafted),
            fault == "unknown-schema" ? 99 : 2, payload, fault == "null-metadata" ? "null" : null);
        await FluentActions.Awaiting(() => Reader(source).ReadAsync(Stream, 1, CancellationToken.None))
            .Should().ThrowAsync<EventStreamReadException>();
    }

    [Fact]
    public async Task Missing_streams_return_an_empty_complete_window()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var page = await Reader(source).ReadAsync(Stream, 500, CancellationToken.None);
        page.Events.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_limits_are_rejected(int limit)
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        await FluentActions.Awaiting(() => Reader(source).ReadAsync(Stream, limit, CancellationToken.None))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        await using var source = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await FluentActions.Awaiting(() => Reader(source).ReadAsync(Stream, 500, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    private static PostgresBoundedEventStreamReader Reader(NpgsqlDataSource source)
        => new(new NpgsqlConnectionFactory(source), EventStoreJsonOptions.Create(),
            new EventUpcasterPipeline(new EventTypeRegistry().Register<OrderDrafted>(), [new OrderDraftedV1ToV2()]));

    private static async Task SeedAsync(NpgsqlDataSource source, StreamId stream, int version, string type, int schema, string payload, string? metadataJson = null)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO event_store.events
                (stream_id, stream_version, event_id, event_type, event_version, payload, metadata, occurred_utc)
            VALUES (@stream, @version, @event, @type, @schema, @payload, @metadata, @occurred)
            """);
        command.Parameters.AddWithValue("stream", stream.Value);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("event", Guid.NewGuid());
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("schema", NpgsqlDbType.Smallint, (short)schema);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb,
            metadataJson ?? JsonSerializer.Serialize(new EventMetadata(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CustomerId,
                "test", At, WellKnownTenants.Default), EventStoreJsonOptions.Create()));
        command.Parameters.AddWithValue("occurred", At);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long[]> CountsAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT (SELECT COUNT(*) FROM event_store.events),
                   (SELECT COUNT(*) FROM event_store.outbox),
                   (SELECT COUNT(*) FROM event_store.snapshots),
                   (SELECT COUNT(*) FROM read_models.projection_checkpoints)
            """);
        await using var rows = await command.ExecuteReaderAsync();
        await rows.ReadAsync();
        return [rows.GetInt64(0), rows.GetInt64(1), rows.GetInt64(2), rows.GetInt64(3)];
    }
}
