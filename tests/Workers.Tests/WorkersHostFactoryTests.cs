using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.OrderList;
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
            eventStoreConnectionString: "Host=localhost;Database=stub",
            readModelConnectionString: "Host=localhost;Database=stub");

        host.Services.GetRequiredService<IEventStore>()
            .Should().BeOfType<PostgresEventStore>();

        // EventTypeRegistry populated via SalesEventTypeProvider: the
        // factory-on-first-resolution path from commit 4 actually fires here.
        var registry = host.Services.GetRequiredService<EventTypeRegistry>();
        registry.NameFor(typeof(OrderPlaced)).Should().Be(nameof(OrderPlaced));
        registry.NameFor(typeof(OrderDrafted)).Should().Be(nameof(OrderDrafted));

        // OrderListProjection is the same instance under every interface its
        // consumers resolve.
        var projection = host.Services.GetRequiredService<OrderListProjection>();
        host.Services.GetRequiredService<IProjection>().Should().BeSameAs(projection);
        host.Services.GetRequiredService<IEventHandler<OrderPlaced>>().Should().BeSameAs(projection);

        // Both hosted services land in the container: ProjectionStartupCatchUpService
        // (the IHostedLifecycleService from commit 6) and OutboxProcessor (the
        // BackgroundService transitively wired by AddPostgresEventStore).
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        hosted.OfType<ProjectionStartupCatchUpService>().Should().ContainSingle();
        hosted.OfType<OutboxProcessor>().Should().ContainSingle();
    }
}
