using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Migration.Demo.Cdc;
using EventSourcingCqrs.Migration.Demo.LegacyOutbox;
using EventSourcingCqrs.Migration.Demo.Strangler;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Migration.Tests;

// S4: the strangler router. It routes each order to the event-sourced application or the legacy CRUD
// service by a predicate, so both implementations run at once. An event-sourced placement is the full
// command sequence a real placement needs (DraftOrder, AddOrderLine, SetOrderShippingAddress,
// PlaceOrder); a legacy placement is the outbox-on-legacy write path.
//
// RED this turn: the router is a no-op, so the matching fact fails on the empty event-sourced stream
// and the non-matching fact fails on the missing legacy row.
public sealed class StranglerRouterTests : IClassFixture<CdcDatabaseFixture>
{
    // The default predicate routes even ids event-sourced, odd ids legacy.
    private const long EventSourcedRoutedId = 2;
    private const long LegacyRoutedId = 1;

    private readonly CdcDatabaseFixture _fixture;

    public StranglerRouterTests(CdcDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Router_places_matching_orders_through_the_event_sourced_side()
    {
        var context = await _fixture.CreateContextAsync();
        var router = new StranglerRouter(
            new LegacyOrderService(context.LegacyConnectionString, context.EventTypes, context.JsonOptions),
            context.CommandBus);

        await router.PlaceOrderAsync(EventSourcedRoutedId, "Ada Lovelace", 42.50m, CancellationToken.None);
        await router.MarkOrderPaidAsync(EventSourcedRoutedId, CancellationToken.None);

        var stream = await ReadStreamAsync(context, EventSourcedRoutedId);
        stream.Select(e => e.Payload.GetType()).Should().Equal(
            typeof(OrderDrafted), typeof(OrderLineAdded), typeof(ShippingAddressSet), typeof(OrderPlaced));

        (await OrderStatusAsync(context, EventSourcedRoutedId)).Should().BeNull();
        (await OutboxTypeNamesAsync(context, EventSourcedRoutedId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Router_places_non_matching_orders_through_the_legacy_side()
    {
        var context = await _fixture.CreateContextAsync();
        var router = new StranglerRouter(
            new LegacyOrderService(context.LegacyConnectionString, context.EventTypes, context.JsonOptions),
            context.CommandBus);

        await router.PlaceOrderAsync(LegacyRoutedId, "Grace Hopper", 10.00m, CancellationToken.None);
        await router.MarkOrderPaidAsync(LegacyRoutedId, CancellationToken.None);

        (await OrderStatusAsync(context, LegacyRoutedId)).Should().Be("paid");
        (await OutboxTypeNamesAsync(context, LegacyRoutedId))
            .Should().Equal(nameof(OrderDrafted), nameof(OrderPlaced));
        (await ReadStreamAsync(context, LegacyRoutedId)).Should().BeEmpty();
    }

    private static async Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(CdcTestContext context, long legacyId)
    {
        var stream = StreamId.ForAggregate<Order>(
            WellKnownTenants.Default, LegacyChangeTranslator.OrderIdFor(legacyId));
        return await context.EventStore.ReadStreamAsync(stream, 0, CancellationToken.None);
    }

    private static async Task<string?> OrderStatusAsync(CdcTestContext context, long legacyId)
    {
        await using var connection = new NpgsqlConnection(context.LegacyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM legacy.orders WHERE id = @id";
        command.Parameters.AddWithValue("id", legacyId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<IReadOnlyList<string>> OutboxTypeNamesAsync(CdcTestContext context, long legacyId)
    {
        var aggregateId = LegacyChangeTranslator.OrderIdFor(legacyId);
        await using var connection = new NpgsqlConnection(context.LegacyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT type_name FROM legacy.legacy_outbox WHERE aggregate_id = @aggregate_id ORDER BY outbox_id";
        command.Parameters.AddWithValue("aggregate_id", aggregateId);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
