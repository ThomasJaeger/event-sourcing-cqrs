using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.OrderList;
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
    public void AddReadModels_resolves_the_read_side_service_graph()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresEventStore(opts =>
            opts.ConnectionString = "Host=localhost;Database=stub");
        services.AddReadModels(opts =>
            opts.ConnectionString = "Host=localhost;Database=stub");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IReadModelConnectionFactory>()
            .Should().BeOfType<NpgsqlReadModelConnectionFactory>();
        provider.GetRequiredService<ICheckpointStore>()
            .Should().BeOfType<PostgresCheckpointStore>();
        provider.GetRequiredService<IOrderListStore>()
            .Should().BeOfType<PostgresOrderListStore>();

        // One projection instance, surfaced under every interface its consumers
        // resolve. All four forwarding registrations hand back the same singleton.
        var projection = provider.GetRequiredService<OrderListProjection>();
        provider.GetRequiredService<IProjection>().Should().BeSameAs(projection);
        provider.GetRequiredService<IEventHandler<OrderPlaced>>().Should().BeSameAs(projection);
        provider.GetRequiredService<IEventHandler<OrderShipped>>().Should().BeSameAs(projection);
        provider.GetRequiredService<IEventHandler<OrderCancelled>>().Should().BeSameAs(projection);

        // AddReadModels composes with AddPostgresEventStore: the event-store
        // side still resolves, because AddReadModels does not register an
        // NpgsqlDataSource and so cannot collide with the event store's.
        provider.GetRequiredService<IEventStore>().Should().BeOfType<PostgresEventStore>();
        provider.GetRequiredService<IMessageDispatcher>()
            .Should().BeOfType<InProcessMessageDispatcher>();
        provider.GetServices<IHostedService>().OfType<OutboxProcessor>()
            .Should().ContainSingle();
    }
}
