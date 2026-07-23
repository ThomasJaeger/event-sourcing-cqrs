using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Migration.Demo.Cdc;
using EventSourcingCqrs.Migration.Demo.LegacyOutbox;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Migration.Tests;

// S3: the outbox-on-legacy pattern. The legacy order service writes its CRUD row and a serialized
// domain event to an outbox table in one transaction; an emitter drains the outbox into the event
// store. Unlike CDC, the app writes the event explicitly rather than a trigger tracking the row.
//
// RED this turn: the service and emitter are no-ops, so Fact A fails on the absent legacy.legacy_outbox
// and Fact B fails on the empty stream.
public sealed class LegacyOutboxTests : IClassFixture<CdcDatabaseFixture>
{
    private const long OrderId = 1;

    private readonly CdcDatabaseFixture _fixture;

    public LegacyOutboxTests(CdcDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Legacy_service_writes_land_the_order_and_the_outbox_row_together()
    {
        var context = await _fixture.CreateContextAsync();
        var service = new LegacyOrderService(
            context.LegacyConnectionString, context.EventTypes, context.JsonOptions);

        await service.PlaceOrderAsync(OrderId, "Ada Lovelace", 42.50m, CancellationToken.None);

        var aggregateId = LegacyChangeTranslator.OrderIdFor(OrderId);
        var outbox = await ReadOutboxAsync(context, aggregateId);
        outbox.Should().ContainSingle();
        outbox[0].TypeName.Should().Be(nameof(OrderDrafted));
        outbox[0].EmittedUtc.Should().BeNull();
        JsonDocument.Parse(outbox[0].Payload).RootElement.GetProperty("order_id").GetGuid()
            .Should().Be(aggregateId);

        (await OrderExistsAsync(context, OrderId)).Should().BeTrue();
    }

    [Fact]
    public async Task Outbox_emitter_appends_events_and_a_redrain_emits_nothing_new()
    {
        var context = await _fixture.CreateContextAsync();
        var service = new LegacyOrderService(
            context.LegacyConnectionString, context.EventTypes, context.JsonOptions);
        await service.PlaceOrderAsync(OrderId, "Ada Lovelace", 42.50m, CancellationToken.None);
        await service.MarkOrderPaidAsync(OrderId, CancellationToken.None);

        var emitter = new LegacyOutboxEmitter(
            context.LegacyConnectionString, context.EventStore, context.SchemaVersions,
            context.EventTypes, context.JsonOptions);
        await emitter.DrainAsync(CancellationToken.None);

        var events = await ReadStreamAsync(context);
        events.Select(e => e.Payload.GetType()).Should().Equal(typeof(OrderDrafted), typeof(OrderPlaced));

        await emitter.DrainAsync(CancellationToken.None);
        (await ReadStreamAsync(context)).Count.Should().Be(events.Count);
    }

    private static async Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(CdcTestContext context)
    {
        var stream = StreamId.ForAggregate<Order>(
            WellKnownTenants.Default, LegacyChangeTranslator.OrderIdFor(OrderId));
        return await context.EventStore.ReadStreamAsync(stream, 0, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<OutboxRow>> ReadOutboxAsync(CdcTestContext context, Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(context.LegacyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT type_name, payload, emitted_utc FROM legacy.legacy_outbox "
            + "WHERE aggregate_id = @aggregate_id ORDER BY outbox_id";
        command.Parameters.AddWithValue("aggregate_id", aggregateId);

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(
                reader.GetString(0),
                reader.GetFieldValue<string>(1),
                await reader.IsDBNullAsync(2) ? null : reader.GetDateTime(2)));
        }

        return rows;
    }

    private static async Task<bool> OrderExistsAsync(CdcTestContext context, long orderId)
    {
        await using var connection = new NpgsqlConnection(context.LegacyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM legacy.orders WHERE id = @id";
        command.Parameters.AddWithValue("id", orderId);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    private sealed record OutboxRow(string TypeName, string Payload, DateTime? EmittedUtc);
}
