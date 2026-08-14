using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace EventSourcingCqrs.Infrastructure.Tests.Kurrent;

// KurrentDB backing node for the contract suite. Unlike the relational fixtures, which hold one
// container per class and hand out a fresh database per fact, this hands out a fresh CONTAINER per
// fact. KurrentDB's $all feed is node-global with no per-database isolation, and three core facts
// assert over the whole feed (excludes-PM, tenant-window, resume-from-position), so the only honest
// isolation is a fresh node each time (Phase 13 slice 1 isolation ruling). Cold start to healthy is
// about 5.6s, which is the per-fact cost this buys.
//
// mem-db insecure single node, matching the spike: no on-disk state to clean up, and no TLS to
// configure for a throwaway node the test reaches over a mapped port.
public sealed class KurrentFixture
{
    public async Task<IContainer> StartNodeAsync()
    {
        var container = new ContainerBuilder("kurrentplatform/kurrentdb:26.0.2")
            .WithCommand("--insecure", "--mem-db")
            .WithPortBinding(2113, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
            .Build();

        await container.StartAsync();
        return container;
    }
}
