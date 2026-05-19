using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class CommandContextTests
{
    [Fact]
    public void System_carries_deterministic_stub_values()
    {
        var system = CommandContext.System;

        system.CorrelationId.Should().Be(Guid.Empty);
        system.CausationCommandId.Should().Be(Guid.Empty);
        system.ActorId.Should().Be(Guid.Empty);
        system.ServiceName.Should().Be("Workers");
        system.UtcNow().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AsyncLocalCommandContextAccessor_isolates_value_across_concurrent_async_flows()
    {
        var accessor = new AsyncLocalCommandContextAccessor();

        var first = RunFlow(accessor, new CommandContext
        {
            CorrelationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CausationCommandId = Guid.Empty,
            ActorId = Guid.Empty,
            ServiceName = "test"
        });
        var second = RunFlow(accessor, new CommandContext
        {
            CorrelationId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            CausationCommandId = Guid.Empty,
            ActorId = Guid.Empty,
            ServiceName = "test"
        });

        var results = await Task.WhenAll(first, second);

        results[0].Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        results[1].Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        accessor.Current.Should().BeNull();
    }

    private static async Task<Guid> RunFlow(ICommandContextAccessor accessor, CommandContext context)
    {
        accessor.Current = context;
        await Task.Yield();
        await Task.Delay(10);
        var observed = accessor.Current!.CorrelationId;
        accessor.Current = null;
        return observed;
    }
}
