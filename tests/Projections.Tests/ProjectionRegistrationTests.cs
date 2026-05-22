using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Projections.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class ProjectionRegistrationTests
{
    [Fact]
    public void AddProjection_surfaces_the_projection_under_every_interface_as_one_singleton()
    {
        var services = new ServiceCollection();
        services.AddProjection<TestProjection>();
        using var provider = services.BuildServiceProvider();

        var projection = provider.GetRequiredService<TestProjection>();
        provider.GetRequiredService<IProjection>().Should().BeSameAs(projection);
        provider.GetRequiredService<IEventHandler<TestEventA>>().Should().BeSameAs(projection);
        provider.GetRequiredService<IEventHandler<TestEventB>>().Should().BeSameAs(projection);
    }

    [Fact]
    public void AddProjection_registers_one_handler_per_implemented_event_and_no_others()
    {
        var services = new ServiceCollection();
        services.AddProjection<TestProjection>();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IEventHandler<TestEventA>>().Should().ContainSingle();
        provider.GetServices<IEventHandler<TestEventB>>().Should().ContainSingle();
        // The projection subscribes to A and B but not C: no forwarding for C.
        provider.GetServices<IEventHandler<TestEventC>>().Should().BeEmpty();
    }

    private sealed record TestEventA : IDomainEvent;
    private sealed record TestEventB : IDomainEvent;
    private sealed record TestEventC : IDomainEvent;

    private sealed class TestProjection
        : IProjection, IEventHandler<TestEventA>, IEventHandler<TestEventB>
    {
        public string Name => "test-projection";

        public Task HandleAsync(EventContext<TestEventA> context, CancellationToken ct)
            => Task.CompletedTask;

        public Task HandleAsync(EventContext<TestEventB> context, CancellationToken ct)
            => Task.CompletedTask;
    }
}
