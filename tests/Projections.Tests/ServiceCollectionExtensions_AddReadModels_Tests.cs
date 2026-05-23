using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.CustomerSummary;
using EventSourcingCqrs.Projections.InventoryDashboard;
using EventSourcingCqrs.Projections.OrderDetail;
using EventSourcingCqrs.Projections.OrderIdToPaymentId;
using EventSourcingCqrs.Projections.OrderList;
using EventSourcingCqrs.Projections.SkuToInventoryId;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class ServiceCollectionExtensions_AddReadModels_Tests
{
    [Fact]
    public async Task AddReadModels_resolves_the_read_side_service_graph()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresEventStore(opts =>
            opts.ConnectionString = "Host=localhost;Database=stub");
        services.AddReadModels(opts =>
            opts.ConnectionString = "Host=localhost;Database=stub");

        // await using on the provider: NpgsqlReadModelConnectionFactory is an
        // IAsyncDisposable-only singleton, and ServiceProvider.Dispose throws
        // on such a singleton to make the misuse explicit.
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IReadModelConnectionFactory>()
            .Should().BeOfType<NpgsqlReadModelConnectionFactory>();
        provider.GetRequiredService<ICheckpointStore>()
            .Should().BeOfType<PostgresCheckpointStore>();
        provider.GetRequiredService<IOrderListStore>()
            .Should().BeOfType<PostgresOrderListStore>();

        // Each projection is one instance surfaced under every interface its
        // consumers resolve; the forwarding registrations hand back the same
        // singleton. Four projections now, so the IProjection set holds all.
        var orderList = provider.GetRequiredService<OrderListProjection>();
        var customerSummary = provider.GetRequiredService<CustomerSummaryProjection>();
        var orderDetail = provider.GetRequiredService<OrderDetailProjection>();
        // OrderPlaced and OrderCancelled now have three handlers (OrderList,
        // CustomerSummary, OrderDetail); the dispatcher fans out to all three via
        // GetServices.
        provider.GetServices<IEventHandler<OrderPlaced>>()
            .Should().Contain(orderList).And.Contain(customerSummary).And.Contain(orderDetail);
        provider.GetServices<IEventHandler<OrderCancelled>>()
            .Should().Contain(orderList).And.Contain(customerSummary).And.Contain(orderDetail);
        // OrderShipped now has two handlers (OrderList and OrderDetail); OrderDrafted
        // and the other line and address events are OrderDetail-only.
        provider.GetServices<IEventHandler<OrderShipped>>()
            .Should().Contain(orderList).And.Contain(orderDetail);
        provider.GetRequiredService<IEventHandler<OrderDrafted>>().Should().BeSameAs(orderDetail);

        var skuToInventory = provider.GetRequiredService<SkuToInventoryIdProjection>();
        var inventoryDashboard = provider.GetRequiredService<InventoryDashboardProjection>();
        // InventoryCreated now has two handlers (SkuToInventoryId and
        // InventoryDashboard); the other three Inventory events are
        // InventoryDashboard-only and stay on GetRequiredService.
        provider.GetServices<IEventHandler<InventoryCreated>>()
            .Should().Contain(skuToInventory).And.Contain(inventoryDashboard);
        provider.GetRequiredService<IEventHandler<InventoryAdjusted>>().Should().BeSameAs(inventoryDashboard);
        provider.GetRequiredService<IEventHandler<InventoryReserved>>().Should().BeSameAs(inventoryDashboard);
        provider.GetRequiredService<IEventHandler<InventoryReleased>>().Should().BeSameAs(inventoryDashboard);

        var orderToPayment = provider.GetRequiredService<OrderIdToPaymentIdProjection>();
        provider.GetRequiredService<IEventHandler<PaymentAuthorized>>().Should().BeSameAs(orderToPayment);

        var projections = provider.GetServices<IProjection>().ToList();
        projections.Should().HaveCount(6);
        projections.Should().Contain(orderList);
        projections.Should().Contain(customerSummary);
        projections.Should().Contain(inventoryDashboard);
        projections.Should().Contain(skuToInventory);
        projections.Should().Contain(orderToPayment);
        projections.Should().Contain(orderDetail);

        // AddReadModels composes with AddPostgresEventStore: the event-store
        // side still resolves, because AddReadModels does not register an
        // NpgsqlDataSource and so cannot collide with the event store's.
        provider.GetRequiredService<IEventStore>().Should().BeOfType<PostgresEventStore>();
        provider.GetRequiredService<IMessageDispatcher>()
            .Should().BeOfType<InProcessMessageDispatcher>();
        provider.GetServices<IHostedService>().OfType<OutboxProcessor>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task Disposing_the_provider_disposes_the_read_model_connection_factory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresEventStore(opts =>
            opts.ConnectionString = "Host=localhost;Database=stub");
        services.AddReadModels(opts =>
            opts.ConnectionString = "Host=localhost;Database=stub");
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IReadModelConnectionFactory>();

        // ServiceProvider implements IAsyncDisposable; the async disposal path
        // is what triggers IAsyncDisposable.DisposeAsync on singletons.
        await provider.DisposeAsync();

        // After provider disposal the factory's underlying NpgsqlDataSource is
        // gone; further OpenConnectionAsync calls throw. Proves the container
        // walked from the IReadModelConnectionFactory singleton (registered as
        // the interface) to the concrete's IAsyncDisposable.
        await factory.Invoking(f => f.OpenConnectionAsync(CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
    }
}
