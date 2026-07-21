using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public class AddApplicationTests
{
    [Fact]
    public void AddApplication_registers_IProcessManagerRepository_as_open_generic()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddApplication();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IProcessManagerRepository<TestPm>>()
            .Should().BeOfType<ProcessManagerRepository<TestPm>>();
    }

    [Fact]
    public void AddApplication_populates_QueryTypeRegistry_from_registered_providers()
    {
        var services = new ServiceCollection();
        // Providers may land after AddApplication: the walk enumerates
        // GetServices<IQueryTypeProvider>() at first resolution, not at
        // registration time, mirroring the event-store registry walks.
        services.AddApplication();
        services.AddSingleton<IQueryTypeProvider, SalesQueryTypeProvider>();
        services.AddSingleton<IQueryTypeProvider, FulfillmentQueryTypeProvider>();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<QueryTypeRegistry>();

        registry.TypeFor(nameof(ListOrders)).Should().Be(typeof(ListOrders));
        registry.TypeFor(nameof(GetAllInventoryDashboard))
            .Should().Be(typeof(GetAllInventoryDashboard));
        registry.EnumerateQueries().Should().HaveCount(6);
    }

    [Fact]
    public void AddApplication_does_not_throw_when_every_command_declares_a_permission()
    {
        var services = new ServiceCollection();

        var act = () => services.AddApplication();

        act.Should().NotThrow();
    }

    // S2b composition characterizations (green on write): the closed-generic override wins for Order
    // and the open generic still serves every other aggregate. Container-free, per the convention
    // above; the teeth (comment out the override, watch these fail) confirm the override is what
    // makes them pass.
    [Fact]
    public void AddApplication_resolves_the_Order_repository_as_the_snapshotting_variant()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<ICurrentEventSchemaVersions, StubCurrentVersions>();
        services.AddSingleton<ISnapshotStore>(new RecordingSnapshotStore());
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        services.AddApplication();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IEventStoreRepository<Order>>()
            .Should().BeOfType<SnapshottingEventStoreRepository<Order, OrderSnapshot>>();
    }

    [Fact]
    public void AddApplication_resolves_a_memento_less_aggregate_repository_as_the_plain_variant()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<ICurrentEventSchemaVersions, StubCurrentVersions>();
        services.AddApplication();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Inventory has no snapshot seam, so it closes the open generic rather than the Order override.
        scope.ServiceProvider.GetRequiredService<IEventStoreRepository<Inventory>>()
            .Should().BeOfType<EventStoreRepository<Inventory>>();
    }

    private sealed class TestPm : ProcessManager
    {
        public TestPm(StreamId streamId) : base(streamId) { }

        protected override void Apply(IProcessManagerEvent @event) { }
    }
}
