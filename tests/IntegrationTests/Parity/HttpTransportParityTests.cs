using System.Text.Json;
using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.IntegrationTests.Commands;
using EventSourcingCqrs.IntegrationTests.Commands.Billing;
using EventSourcingCqrs.IntegrationTests.Commands.Fulfillment;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Parity;

// PLAN done-when: the Web and Api surfaces produce the same effect. The Web host
// dispatches through the Api over HTTP (D-W), so the parity surface is "HTTP
// transport produces the same events as a direct bus dispatch." Each fact seeds
// identical pre-state on two aggregate ids, dispatches one command via POST
// /commands and the same command via ICommandBus.SendAsync, and asserts the two
// streams carry the same event-type sequence. The round-trip theory pins the
// other half: a command serialized to the envelope and registry-deserialized
// equals the original, so the transport cannot silently alter a command's fields.
public sealed class HttpTransportParityTests : IClassFixture<ApiFixture>
{
    private static readonly DateTime Seed = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly ApiFixture _fixture;

    public HttpTransportParityTests(ApiFixture fixture) => _fixture = fixture;

    // Sales

    [Fact]
    public async Task DraftOrder_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();

        await PostAsync("DraftOrder", new { orderId = httpId, customerId = Guid.NewGuid() });
        await DispatchAsync(new DraftOrder(busId, Guid.NewGuid()));

        await AssertParityAsync(OrderStream(httpId), OrderStream(busId), "OrderDrafted");
    }

    [Fact]
    public async Task AddOrderLine_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await SeedDraftedAsync(httpId);
        await SeedDraftedAsync(busId);

        await PostAsync("AddOrderLine", new
        {
            orderId = httpId,
            lineId = Guid.NewGuid(),
            sku = "SKU-ADD-2",
            quantity = 2,
            unitPrice = new { amount = 12.50m, currency = new { code = "USD" } },
        });
        await DispatchAsync(new AddOrderLine(busId, Guid.NewGuid(), "SKU-ADD-2", 2, new Money(12.50m, Currency.USD)));

        await AssertParityAsync(OrderStream(httpId), OrderStream(busId), "OrderDrafted", "OrderLineAdded");
    }

    [Fact]
    public async Task RemoveOrderLine_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var httpLine = Guid.NewGuid();
        var busLine = Guid.NewGuid();
        await SeedDraftedWithLineAsync(httpId, httpLine, "SKU-RM-3");
        await SeedDraftedWithLineAsync(busId, busLine, "SKU-RM-3");

        await PostAsync("RemoveOrderLine", new { orderId = httpId, lineId = httpLine });
        await DispatchAsync(new RemoveOrderLine(busId, busLine));

        await AssertParityAsync(
            OrderStream(httpId), OrderStream(busId), "OrderDrafted", "OrderLineAdded", "OrderLineRemoved");
    }

    [Fact]
    public async Task SetOrderShippingAddress_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await SeedDraftedAsync(httpId);
        await SeedDraftedAsync(busId);

        await PostAsync("SetOrderShippingAddress", new
        {
            orderId = httpId,
            shippingAddress = new { street = "2 Pine", city = "Tahoe", postalCode = "96150", country = "US" },
        });
        await DispatchAsync(new SetOrderShippingAddress(busId, new Address("2 Pine", "Tahoe", "96150", "US")));

        await AssertParityAsync(OrderStream(httpId), OrderStream(busId), "OrderDrafted", "ShippingAddressSet");
    }

    [Fact]
    public async Task PlaceOrder_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await SeedDraftLineAddressAsync(httpId);
        await SeedDraftLineAddressAsync(busId);

        await PostAsync("PlaceOrder", new { orderId = httpId });
        await DispatchAsync(new PlaceOrder(busId));

        await AssertParityAsync(OrderStream(httpId), OrderStream(busId),
            "OrderDrafted", "OrderLineAdded", "ShippingAddressSet", "OrderPlaced");
    }

    [Fact]
    public async Task ShipOrder_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await SeedPlacedAsync(httpId);
        await SeedPlacedAsync(busId);

        await PostAsync("ShipOrder", new { orderId = httpId, carrier = "UPS", trackingNumber = "TRK-6" });
        await DispatchAsync(new ShipOrder(busId, "UPS", "TRK-6"));

        await AssertParityAsync(OrderStream(httpId), OrderStream(busId),
            "OrderDrafted", "OrderLineAdded", "ShippingAddressSet", "OrderPlaced", "OrderShipped");
    }

    [Fact]
    public async Task CancelOrder_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await SeedDraftedAsync(httpId);
        await SeedDraftedAsync(busId);

        await PostAsync("CancelOrder", new { orderId = httpId, reason = "changed mind 7", issuedByUserId = Guid.NewGuid() });
        await DispatchAsync(new CancelOrder(busId, "changed mind 7", Guid.NewGuid()));

        await AssertParityAsync(OrderStream(httpId), OrderStream(busId), "OrderDrafted", "OrderCancelled");
    }

    [Fact]
    public async Task MarkOrderCompleted_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await SeedPlacedAsync(httpId);
        await SeedPlacedAsync(busId);

        await PostAsync("MarkOrderCompleted", new { orderId = httpId });
        await DispatchAsync(new MarkOrderCompleted(busId));

        await AssertParityAsync(OrderStream(httpId), OrderStream(busId),
            "OrderDrafted", "OrderLineAdded", "ShippingAddressSet", "OrderPlaced", "OrderCompleted");
    }

    // Fulfillment

    [Fact]
    public async Task CreateInventory_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();

        await PostAsync("CreateInventory", new { inventoryId = httpId, sku = "SKU-CREATE-9-H" });
        await DispatchAsync(new CreateInventory(busId, "SKU-CREATE-9-B"));

        await AssertParityAsync(InventoryStream(httpId), InventoryStream(busId), "InventoryCreated");
    }

    [Fact]
    public async Task AdjustInventory_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await SeedInventoryAsync(httpId, "SKU-ADJ-10-H");
        await SeedInventoryAsync(busId, "SKU-ADJ-10-B");

        await PostAsync("AdjustInventory", new { inventoryId = httpId, quantityDelta = 7, reason = "restock 10" });
        await DispatchAsync(new AdjustInventory(busId, 7, "restock 10"));

        await AssertParityAsync(InventoryStream(httpId), InventoryStream(busId), "InventoryCreated", "InventoryAdjusted");
    }

    [Fact]
    public async Task DispatchShipment_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await ShipmentSeed.ScheduledAsync(EventStore, httpId);
        await ShipmentSeed.ScheduledAsync(EventStore, busId);

        await PostAsync("DispatchShipment", new { shipmentId = httpId, carrierReference = "REF-11" });
        await DispatchAsync(new DispatchShipment(busId, "REF-11"));

        await AssertParityAsync(ShipmentStream(httpId), ShipmentStream(busId), "ShipmentScheduled", "ShipmentDispatched");
    }

    [Fact]
    public async Task DeliverShipment_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await ShipmentSeed.DispatchedAsync(EventStore, httpId);
        await ShipmentSeed.DispatchedAsync(EventStore, busId);

        await PostAsync("DeliverShipment", new { shipmentId = httpId });
        await DispatchAsync(new DeliverShipment(busId));

        await AssertParityAsync(ShipmentStream(httpId), ShipmentStream(busId),
            "ShipmentScheduled", "ShipmentDispatched", "ShipmentDelivered");
    }

    [Fact]
    public async Task ReturnShipment_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await ShipmentSeed.DeliveredAsync(EventStore, httpId);
        await ShipmentSeed.DeliveredAsync(EventStore, busId);

        await PostAsync("ReturnShipment", new { shipmentId = httpId, reason = "defective 13" });
        await DispatchAsync(new ReturnShipment(busId, "defective 13"));

        await AssertParityAsync(ShipmentStream(httpId), ShipmentStream(busId),
            "ShipmentScheduled", "ShipmentDispatched", "ShipmentDelivered", "ShipmentReturned");
    }

    // Billing

    [Fact]
    public async Task RefundPayment_through_http_and_bus_produce_the_same_event_sequence()
    {
        var httpId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await PaymentSeed.CapturedAsync(EventStore, httpId);
        await PaymentSeed.CapturedAsync(EventStore, busId);

        await PostAsync("RefundPayment", new { paymentId = httpId, reason = "returned 14" });
        await DispatchAsync(new RefundPayment(busId, "returned 14"));

        await AssertParityAsync(PaymentStream(httpId), PaymentStream(busId),
            "PaymentAuthorized", "PaymentCaptured", "PaymentRefunded");
    }

    // Envelope round-trip contract (all 14 commands)

    public static IEnumerable<object[]> UserDispatchedCommands() =>
    [
        [new DraftOrder(Guid.NewGuid(), Guid.NewGuid())],
        [new AddOrderLine(Guid.NewGuid(), Guid.NewGuid(), "SKU-RT-2", 3, new Money(12.34m, Currency.USD))],
        [new RemoveOrderLine(Guid.NewGuid(), Guid.NewGuid())],
        [new SetOrderShippingAddress(Guid.NewGuid(), new Address("3 RT St", "RTville", "11111", "US"))],
        [new PlaceOrder(Guid.NewGuid())],
        [new ShipOrder(Guid.NewGuid(), "RT-Carrier", "RT-TRK-6")],
        [new CancelOrder(Guid.NewGuid(), "round-trip 7", Guid.NewGuid())],
        [new MarkOrderCompleted(Guid.NewGuid())],
        [new CreateInventory(Guid.NewGuid(), "SKU-RT-9")],
        [new AdjustInventory(Guid.NewGuid(), 9, "round-trip 10")],
        [new DispatchShipment(Guid.NewGuid(), "RT-REF-11")],
        [new DeliverShipment(Guid.NewGuid())],
        [new ReturnShipment(Guid.NewGuid(), "round-trip 13")],
        [new RefundPayment(Guid.NewGuid(), "round-trip 14")],
    ];

    [Theory]
    [MemberData(nameof(UserDispatchedCommands))]
    public void Command_survives_the_envelope_round_trip(ICommand command)
    {
        var registry = _fixture.Factory.Services.GetRequiredService<CommandTypeRegistry>();
        var typeName = registry.NameFor(command.GetType());

        var json = JsonSerializer.Serialize(command, command.GetType(), JsonSerializerOptions.Web);
        var roundTripped = (ICommand)JsonSerializer.Deserialize(
            json, registry.TypeFor(typeName), JsonSerializerOptions.Web)!;

        roundTripped.Should().Be(command);
    }

    // Helpers

    private IEventStore EventStore => _fixture.Factory.Services.GetRequiredService<IEventStore>();

    private async Task PostAsync(string type, object payload)
    {
        var response = await _fixture.Factory.CreateClient()
            .PostCommandAsync(type, payload, Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
    }

    private Task DispatchAsync(ICommand command) =>
        _fixture.Factory.Services.GetRequiredService<ICommandBus>()
            .SendAsync(command, Guid.NewGuid().ToString(), CancellationToken.None);

    private async Task AssertParityAsync(StreamId httpStream, StreamId busStream, params string[] expected)
    {
        (await EventTypesAsync(httpStream)).Should().Equal(expected);
        (await EventTypesAsync(busStream)).Should().Equal(expected);
    }

    private async Task<IReadOnlyList<string>> EventTypesAsync(StreamId stream)
    {
        var envelopes = await EventStore.ReadStreamAsync(stream, fromVersion: 0);
        return envelopes.Select(e => e.EventType).ToList();
    }

    private Task SeedAsync(StreamId stream, params IDomainEvent[] events) =>
        EventStoreSeed.AppendAsync(EventStore, stream, events);

    private Task SeedDraftedAsync(Guid id) =>
        SeedAsync(OrderStream(id), new OrderDrafted(id, Guid.NewGuid(), Seed, "web"));

    private Task SeedDraftedWithLineAsync(Guid id, Guid lineId, string sku) =>
        SeedAsync(OrderStream(id),
            new OrderDrafted(id, Guid.NewGuid(), Seed, "web"),
            new OrderLineAdded(id, lineId, sku, 1, new Money(10m, Currency.USD), Seed));

    private Task SeedDraftLineAddressAsync(Guid id) =>
        SeedAsync(OrderStream(id),
            new OrderDrafted(id, Guid.NewGuid(), Seed, "web"),
            new OrderLineAdded(id, Guid.NewGuid(), "SKU-SEED", 1, new Money(10m, Currency.USD), Seed),
            new ShippingAddressSet(id, new Address("1 Seed St", "Seedville", "00000", "US"), Seed));

    private Task SeedPlacedAsync(Guid id) =>
        SeedAsync(OrderStream(id),
            new OrderDrafted(id, Guid.NewGuid(), Seed, "web"),
            new OrderLineAdded(id, Guid.NewGuid(), "SKU-SEED", 1, new Money(10m, Currency.USD), Seed),
            new ShippingAddressSet(id, new Address("1 Seed St", "Seedville", "00000", "US"), Seed),
            new OrderPlaced(id, Guid.NewGuid(), new Money(10m, Currency.USD), Seed));

    private Task SeedInventoryAsync(Guid id, string sku) =>
        SeedAsync(InventoryStream(id),
            new Domain.Fulfillment.Events.InventoryCreated(id, sku, Seed));

    private static StreamId OrderStream(Guid id) => StreamId.ForAggregate<Order>(WellKnownTenants.Default, id);
    private static StreamId InventoryStream(Guid id) => StreamId.ForAggregate<Inventory>(WellKnownTenants.Default, id);
    private static StreamId ShipmentStream(Guid id) => StreamId.ForAggregate<Shipment>(WellKnownTenants.Default, id);
    private static StreamId PaymentStream(Guid id) => StreamId.ForAggregate<Payment>(WellKnownTenants.Default, id);
}
