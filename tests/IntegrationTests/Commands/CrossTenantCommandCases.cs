using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.IntegrationTests.Commands.Billing;
using EventSourcingCqrs.IntegrationTests.Commands.Fulfillment;
using EventSourcingCqrs.IntegrationTests.Commands.Sales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.IntegrationTests.Commands;

// The single home of each registered command type's cross-tenant isolation case (ADR 0031's
// coverage mandate), the command-boundary twin of CrossTenantQueryCases. One lambda per command
// plants a same-id aggregate under the other tenant, establishes the command's precondition under
// the default tenant, dispatches the command as the default tenant, and asserts the command's event
// landed on the default-tenant stream while the other tenant's same-id stream stays exactly its
// seeded creating event. Each lambda runs on a fresh aggregate id so the cases do not collide in the
// shared database; the meta-test runs them all and asserts every registered command has one.
internal static class CrossTenantCommandCases
{
    private static readonly TenantId OtherTenant =
        TenantId.From(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly DateTime SeededAt = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Address TwinDestination = new("1 Test St", "Reno", "89501", "US");
    private static readonly Money TwinAmount = new(50.00m, Currency.USD);
    private static readonly Func<HttpClient, IEventStore, Task> NoPrecondition =
        (_, _) => Task.CompletedTask;

    // One entry per registered command type. A registered type with no entry here is what the
    // meta-test's completeness check flags, so a new command that ships without a case fails the build.
    public static IReadOnlyDictionary<Type, Func<Task>> For(ApiFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return new Dictionary<Type, Func<Task>>
        {
            [typeof(DraftOrder)] = Case(id =>
                AssertIsolatesAsync<Order>(fixture, id, OrderTwin(id), NoPrecondition,
                    "DraftOrder", new { orderId = id, customerId = Guid.NewGuid() }, "OrderDrafted")),

            [typeof(AddOrderLine)] = Case(id =>
                AssertIsolatesAsync<Order>(fixture, id, OrderTwin(id),
                    (client, _) => OrderSetup.DraftAsync(client, id),
                    "AddOrderLine",
                    new
                    {
                        orderId = id,
                        lineId = Guid.NewGuid(),
                        sku = "SKU-001",
                        quantity = 1,
                        unitPrice = new { amount = 10.00m, currency = new { code = "USD" } },
                    },
                    "OrderLineAdded")),

            [typeof(RemoveOrderLine)] = Case(id =>
            {
                var lineId = Guid.NewGuid();
                return AssertIsolatesAsync<Order>(fixture, id, OrderTwin(id),
                    (client, _) => OrderSetup.DraftWithLineAsync(client, id, lineId),
                    "RemoveOrderLine", new { orderId = id, lineId }, "OrderLineRemoved");
            }),

            [typeof(SetOrderShippingAddress)] = Case(id =>
                AssertIsolatesAsync<Order>(fixture, id, OrderTwin(id),
                    (client, _) => OrderSetup.DraftAsync(client, id),
                    "SetOrderShippingAddress",
                    new
                    {
                        orderId = id,
                        shippingAddress = new
                        {
                            street = "1 Test St",
                            city = "Reno",
                            postalCode = "89501",
                            country = "US",
                        },
                    },
                    "ShippingAddressSet")),

            [typeof(PlaceOrder)] = Case(id =>
                AssertIsolatesAsync<Order>(fixture, id, OrderTwin(id),
                    (client, _) => OrderSetup.DraftWithLineAndAddressAsync(client, id),
                    "PlaceOrder", new { orderId = id }, "OrderPlaced")),

            [typeof(ShipOrder)] = Case(id =>
                AssertIsolatesAsync<Order>(fixture, id, OrderTwin(id),
                    (client, _) => OrderSetup.PlaceOrderAsync(client, id),
                    "ShipOrder",
                    new { orderId = id, carrier = "TestCarrier", trackingNumber = "TRK-001" },
                    "OrderShipped")),

            [typeof(CancelOrder)] = Case(id =>
                AssertIsolatesAsync<Order>(fixture, id, OrderTwin(id),
                    (client, _) => OrderSetup.DraftAsync(client, id),
                    "CancelOrder",
                    new { orderId = id, reason = "cross-tenant case", issuedByUserId = Guid.NewGuid() },
                    "OrderCancelled")),

            [typeof(MarkOrderCompleted)] = Case(id =>
                AssertIsolatesAsync<Order>(fixture, id, OrderTwin(id),
                    (client, _) => OrderSetup.PlaceOrderAsync(client, id),
                    "MarkOrderCompleted", new { orderId = id }, "OrderCompleted")),

            [typeof(CreateInventory)] = Case(id =>
                AssertIsolatesAsync<Inventory>(fixture, id, InventoryTwin(id), NoPrecondition,
                    "CreateInventory", new { inventoryId = id, sku = "SKU-NEW" }, "InventoryCreated")),

            [typeof(AdjustInventory)] = Case(id =>
                AssertIsolatesAsync<Inventory>(fixture, id, InventoryTwin(id),
                    (client, _) => InventorySetup.CreateAsync(client, id),
                    "AdjustInventory",
                    new { inventoryId = id, quantityDelta = 10, reason = "initial stock" },
                    "InventoryAdjusted")),

            [typeof(DispatchShipment)] = Case(id =>
                AssertIsolatesAsync<Shipment>(fixture, id, ShipmentTwin(id),
                    (_, eventStore) => ShipmentSeed.ScheduledAsync(eventStore, id),
                    "DispatchShipment", new { shipmentId = id, carrierReference = "UPS-1Z" },
                    "ShipmentDispatched")),

            [typeof(DeliverShipment)] = Case(id =>
                AssertIsolatesAsync<Shipment>(fixture, id, ShipmentTwin(id),
                    (_, eventStore) => ShipmentSeed.DispatchedAsync(eventStore, id),
                    "DeliverShipment", new { shipmentId = id }, "ShipmentDelivered")),

            [typeof(ReturnShipment)] = Case(id =>
                AssertIsolatesAsync<Shipment>(fixture, id, ShipmentTwin(id),
                    (_, eventStore) => ShipmentSeed.DeliveredAsync(eventStore, id),
                    "ReturnShipment", new { shipmentId = id, reason = "damaged on arrival" },
                    "ShipmentReturned")),

            [typeof(RefundPayment)] = Case(id =>
                AssertIsolatesAsync<Payment>(fixture, id, PaymentTwin(id),
                    (_, eventStore) => PaymentSeed.CapturedAsync(eventStore, id),
                    "RefundPayment", new { paymentId = id, reason = "customer dissatisfied" },
                    "PaymentRefunded")),
        };
    }

    // Mints a fresh aggregate id per case so the cases do not collide in the shared database.
    private static Func<Task> Case(Func<Guid, Task> body) => () => body(Guid.NewGuid());

    // The minimal creating event for each aggregate's other-tenant twin.
    private static OrderDrafted OrderTwin(Guid id) => new(id, Guid.NewGuid(), SeededAt, "web");
    private static InventoryCreated InventoryTwin(Guid id) => new(id, "SKU-OTHER", SeededAt);
    private static ShipmentScheduled ShipmentTwin(Guid id) =>
        new(id, Guid.NewGuid(), TwinDestination,
            new[] { new ShipmentLine(Guid.NewGuid(), Guid.NewGuid(), "SKU-001", 1) }, SeededAt);
    private static PaymentAuthorized PaymentTwin(Guid id) =>
        new(id, Guid.NewGuid(), TwinAmount, "pm-ref-test", SeededAt);

    // The locked template: seed the other tenant's same-id twin, establish the default precondition,
    // dispatch the command as the default tenant, then assert the command's event landed on the
    // default stream and the other tenant's stream is untouched (still exactly its creating event,
    // every envelope carrying the other tenant). The second assertion is the discriminator-isolation
    // proof.
    private static async Task AssertIsolatesAsync<TAggregate>(
        ApiFixture fixture,
        Guid id,
        IDomainEvent twinCreatingEvent,
        Func<HttpClient, IEventStore, Task> seedDefaultPrecondition,
        string commandType,
        object payload,
        string resultingEventType)
        where TAggregate : AggregateRoot
    {
        var client = fixture.Factory.CreateClient();
        var eventStore = fixture.Factory.Services.GetRequiredService<IEventStore>();
        var defaultStream = StreamId.ForAggregate<TAggregate>(WellKnownTenants.Default, id);
        var otherStream = StreamId.ForAggregate<TAggregate>(OtherTenant, id);

        await EventStoreSeed.AppendAsync(eventStore, otherStream, [twinCreatingEvent], OtherTenant);
        await seedDefaultPrecondition(client, eventStore);

        var response = await client.PostCommandAsync(
            commandType, payload, idempotencyKey: Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();

        var defaultEnvelopes = await eventStore.ReadStreamAsync(defaultStream, fromVersion: 0);
        defaultEnvelopes.Should().Contain(e => e.EventType == resultingEventType);
        defaultEnvelopes.Should().AllSatisfy(e => e.Metadata.Tenant.Should().Be(WellKnownTenants.Default));

        var otherEnvelopes = await eventStore.ReadStreamAsync(otherStream, fromVersion: 0);
        otherEnvelopes.Should().ContainSingle().Which.Metadata.Tenant.Should().Be(OtherTenant);
    }
}
