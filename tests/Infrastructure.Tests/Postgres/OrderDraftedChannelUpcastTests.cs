using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Tests.TestKit;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// Chapter 11's worked example: the OrderDrafted version-1-to-version-2 lineage. OrderDrafted gained a
// Channel member, and rows written before it lack that member. The done-when (PLAN.md:495) is that a
// stored version-1 row whose payload JSON carries no channel reads back as the current shape with
// Channel set to the "unknown" default and the envelope stamped at the current version. The
// OrderDraftedV1ToV2 upcaster in the pipeline lifts the older payload forward and derives the version
// from the chain, so Channel comes back "unknown" and the version reads 2.
//
// Reads through PostgreSQL, the default engine, straight against a migrated Testcontainers database,
// the shape the sibling PostgresEventStore suites use. The version-1 row is seeded by a direct insert
// of the older payload shape, which the current append path can no longer write.
public class OrderDraftedChannelUpcastTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public OrderDraftedChannelUpcastTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_version_one_order_drafted_row_without_a_channel_reads_as_the_unknown_channel()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var jsonOptions = CreateJsonOptions();
        var registry = new EventTypeRegistry().Register<OrderDrafted>();
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource),
            registry,
            CreatePmRegistry(),
            jsonOptions,
            new EventUpcasterPipeline(registry, [new OrderDraftedV1ToV2()]));

        var streamId = NewStreamId();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var draftedUtc = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);

        // The version-1 payload: what an older revision wrote, three members and no channel. Serialized
        // with the adapter's own options so the stored bytes match a real historical row.
        var v1PayloadJson = JsonSerializer.Serialize(
            new { OrderId = orderId, CustomerId = customerId, DraftedUtc = draftedUtc }, jsonOptions);
        await SeedVersionOneRowAsync(dataSource, streamId, eventId, draftedUtc, v1PayloadJson, jsonOptions);

        var read = await store.ReadStreamAsync(streamId, 0, CancellationToken.None);

        read.Should().HaveCount(1);
        var drafted = read[0].Payload.Should().BeOfType<OrderDrafted>().Which;
        drafted.OrderId.Should().Be(orderId);
        drafted.CustomerId.Should().Be(customerId);
        // The claim of the worked example: the missing channel reads back as the unknown default, not
        // null, and the envelope carries the current schema version.
        drafted.Channel.Should().Be(OrderDrafted.UnknownChannel);
        read[0].EventVersion.Should().Be(2);
    }

    // The write-stamp the lineage exposes. An order drafted through the production write path carries a
    // real channel, but the repository stamps every event at version 1. With the lineage in place,
    // version 1 now means "the v1 shape", so the read resolves OrderDraftedV1, drops the channel the
    // current payload carried, and the upcaster fills its default over the top. The repository must
    // stamp the current version instead, so the read resolves the terminal and the drafted channel
    // survives.
    [Fact]
    public async Task An_order_drafted_through_the_production_path_reads_back_with_its_channel()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var jsonOptions = CreateJsonOptions();
        var registry = new EventTypeRegistry().Register<OrderDrafted>();
        var pipeline = new EventUpcasterPipeline(registry, [new OrderDraftedV1ToV2()]);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource),
            registry,
            CreatePmRegistry(),
            jsonOptions,
            pipeline);
        // The pipeline is also the current-version source, so the write stamps version 2 and the read
        // resolves the terminal directly rather than upcasting a current-shape row.
        var repository = new EventStoreRepository<Order>(
            store, new StubAccessor(), new StubTenantAccessor(), pipeline);

        var orderId = Guid.NewGuid();
        var order = Order.Draft(
            orderId, Guid.NewGuid(), new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc), "phone");
        await repository.SaveAsync(order, CancellationToken.None);

        var streamId = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        var read = await store.ReadStreamAsync(streamId, 0, CancellationToken.None);

        read.Should().ContainSingle();
        var drafted = read[0].Payload.Should().BeOfType<OrderDrafted>().Which;
        drafted.Channel.Should().Be("phone");
        read[0].EventVersion.Should().Be(2);
    }

    // A committed version-1 row inserted straight into the events table, the older payload shape and
    // event_version 1. The current append path stamps the payload's registered name and the current
    // version, so it cannot produce this row; history holds rows it wrote before the member existed.
    private static async Task SeedVersionOneRowAsync(
        NpgsqlDataSource dataSource,
        StreamId streamId,
        Guid eventId,
        DateTime when,
        string payloadJson,
        JsonSerializerOptions jsonOptions)
    {
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: when,
            Tenant: WellKnownTenants.Default);
        var metadataJson = JsonSerializer.Serialize(metadata, jsonOptions);

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.events " +
            "(stream_id, stream_version, event_id, event_type, event_version, " +
            "payload, metadata, occurred_utc) " +
            "VALUES (@stream_id, @stream_version, @event_id, @event_type, @event_version, " +
            "@payload, @metadata, @occurred_utc)";
        cmd.Parameters.AddWithValue("stream_id", NpgsqlDbType.Text, streamId.Value);
        cmd.Parameters.AddWithValue("stream_version", NpgsqlDbType.Integer, 1);
        cmd.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
        cmd.Parameters.AddWithValue("event_type", NpgsqlDbType.Text, nameof(OrderDrafted));
        cmd.Parameters.AddWithValue("event_version", NpgsqlDbType.Smallint, (short)1);
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson);
        cmd.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, metadataJson);
        cmd.Parameters.AddWithValue("occurred_utc", NpgsqlDbType.TimestampTz, when);
        await cmd.ExecuteNonQueryAsync();
    }
}
