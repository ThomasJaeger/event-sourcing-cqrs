using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Projections.CustomerSummary;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.InventoryDashboard;
using EventSourcingCqrs.Projections.OrderDetail;
using EventSourcingCqrs.Projections.OrderList;
using EventSourcingCqrs.Projections.SkuToInventoryId;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventSourcingCqrs.Workers.Tests;

public class WorkersHostFactoryTests
{
    [Fact]
    public void Build_resolves_registered_services()
    {
        // Stub connection strings: no resolution path here opens a connection.
        // NpgsqlDataSource.Create, PostgresEventStore's ctor, and the read-model
        // factories all defer connecting until first use.
        using var host = WorkersHostFactory.Build(
            eventStoreProvider: EventStoreProvider.Postgres,
            eventStoreConnectionString: "Host=localhost;Database=stub",
            readModelConnectionString: "Host=localhost;Database=stub");

        host.Services.GetRequiredService<IEventStore>()
            .Should().BeOfType<PostgresEventStore>();

        // EventTypeRegistry populated via Sales and Fulfillment providers: the
        // factory-on-first-resolution path from commit 4 actually fires here.
        var registry = host.Services.GetRequiredService<EventTypeRegistry>();
        registry.NameFor(typeof(OrderPlaced)).Should().Be(nameof(OrderPlaced));
        registry.NameFor(typeof(OrderDrafted)).Should().Be(nameof(OrderDrafted));
        registry.NameFor(typeof(InventoryCreated)).Should().Be(nameof(InventoryCreated));
        registry.NameFor(typeof(InventoryReserved)).Should().Be(nameof(InventoryReserved));
        registry.NameFor(typeof(ShipmentScheduled)).Should().Be(nameof(ShipmentScheduled));
        registry.NameFor(typeof(ShipmentDelivered)).Should().Be(nameof(ShipmentDelivered));
        registry.NameFor(typeof(PaymentAuthorized)).Should().Be(nameof(PaymentAuthorized));
        registry.NameFor(typeof(PaymentCaptured)).Should().Be(nameof(PaymentCaptured));

        // Each projection is the same instance under every interface its
        // consumers resolve; all land in the IProjection set. OrderPlaced has two
        // handlers now (OrderList and CustomerSummary), resolved via GetServices.
        var orderList = host.Services.GetRequiredService<OrderListProjection>();
        var customerSummary = host.Services.GetRequiredService<CustomerSummaryProjection>();
        var orderDetail = host.Services.GetRequiredService<OrderDetailProjection>();
        host.Services.GetServices<IEventHandler<OrderPlaced>>()
            .Should().Contain(orderList).And.Contain(customerSummary).And.Contain(orderDetail);
        var skuToInventory = host.Services.GetRequiredService<SkuToInventoryIdProjection>();
        var inventoryDashboard = host.Services.GetRequiredService<InventoryDashboardProjection>();
        // InventoryCreated now has two handlers (SkuToInventoryId and InventoryDashboard).
        host.Services.GetServices<IEventHandler<InventoryCreated>>()
            .Should().Contain(skuToInventory).And.Contain(inventoryDashboard);
        host.Services.GetServices<IProjection>()
            .Should().Contain(new IProjection[] { orderList, customerSummary, inventoryDashboard, skuToInventory, orderDetail });

        // The hosted services land in the container: ProjectionStartupCatchUpService
        // (the IHostedLifecycleService from commit 6), OutboxProcessor (wired by
        // AddPostgresOutboxProcessor), and DelayQueueProcessor (wired by
        // AddPostgresDelayQueueProcessor after AddApplication).
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        hosted.OfType<ProjectionStartupCatchUpService>().Should().ContainSingle();
        hosted.OfType<OutboxProcessor>().Should().ContainSingle();
        hosted.OfType<DelayQueueProcessor>().Should().ContainSingle();
    }
}
