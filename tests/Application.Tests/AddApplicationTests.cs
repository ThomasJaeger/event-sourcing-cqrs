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

    private sealed class TestPm : ProcessManager
    {
        public TestPm(StreamId streamId) : base(streamId) { }

        protected override void Apply(IProcessManagerEvent @event) { }
    }
}
