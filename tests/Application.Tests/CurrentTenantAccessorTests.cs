using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class CurrentTenantAccessorTests
{
    private static readonly TenantId TenantA =
        TenantId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly TenantId TenantB =
        TenantId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task AsyncLocalCurrentTenantAccessor_isolates_value_across_concurrent_async_flows()
    {
        var accessor = new AsyncLocalCurrentTenantAccessor();

        var first = RunFlow(accessor, TenantA);
        var second = RunFlow(accessor, TenantB);

        var results = await Task.WhenAll(first, second);

        results[0].Should().Be(TenantA);
        results[1].Should().Be(TenantB);
        accessor.Current.Should().BeNull();
    }

    private static async Task<TenantId?> RunFlow(ICurrentTenantAccessor accessor, TenantId tenant)
    {
        accessor.Current = tenant;
        await Task.Yield();
        await Task.Delay(10);
        var observed = accessor.Current;
        accessor.Current = null;
        return observed;
    }
}
