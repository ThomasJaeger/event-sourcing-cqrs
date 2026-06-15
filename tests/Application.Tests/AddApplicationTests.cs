using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

    private sealed class TestPm : ProcessManager
    {
        public TestPm(StreamId streamId) : base(streamId) { }

        protected override void Apply(IProcessManagerEvent @event) { }
    }
}
