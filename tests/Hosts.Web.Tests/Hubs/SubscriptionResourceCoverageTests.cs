using System.Collections.Concurrent;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Authentication;
using EventSourcingCqrs.Hosts.Web.Hubs;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

// The structural subscription coverage harness (ADR 0031's coverage mandate), re-pointed onto the in-process
// dispatcher after the SignalR hub retired (ADR 0032). The subscription surface is the SubscriptionResourceType
// enum, walked exhaustively: every resource type must have a publishing projection in the routing map
// (completeness), a notification for one tenant must reach only that tenant's subscriber (routing isolation),
// and a subscribe must register under the tenant the authorize ALLOW returns, never a caller-supplied one
// (subscribe-key tenant sourcing). A resource type added to the enum without a route fails the completeness
// gate; a routing or tenant-sourcing regression fails its per-type case.
public class SubscriptionResourceCoverageTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantA = TenantId.From(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly TenantId TenantB = TenantId.From(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    [Fact]
    public async Task Every_subscription_resource_type_is_covered_routing_isolated_and_tenant_sourced()
    {
        var members = Enum.GetValues<SubscriptionResourceType>();

        // Completeness: a resource type subscribable but with no publishing projection would deliver nothing.
        // A new enum member added without a routing-map entry fails here, so it fails the build.
        var routed = ResourceRouting.ProjectionResourceTypes.Values.Distinct();
        members.Should().BeSubsetOf(routed);

        foreach (var resourceType in members)
        {
            await AssertRoutingIsolatedByTenantAsync(resourceType);
            await AssertSubscribeKeySourcedFromAllowTenantAsync(resourceType);
        }
    }

    // A publish for tenant A reaches tenant A's subscriber and not tenant B's subscriber on the same resource
    // id. ResourceKey carries the tenant, so the same id under two tenants is two distinct keys.
    private static async Task AssertRoutingIsolatedByTenantAsync(SubscriptionResourceType resourceType)
    {
        var projectionName = ProjectionFor(resourceType);
        await using var dispatcher = NewDispatcher();
        var a = new Recorder();
        var b = new Recorder();
        using var subA = dispatcher.Subscribe(new ResourceKey(TenantA, resourceType, "resource-1"), a.Handler);
        using var subB = dispatcher.Subscribe(new ResourceKey(TenantB, resourceType, "resource-1"), b.Handler);

        dispatcher.Publish(new NotificationEnvelope(projectionName, "resource-1", "Changed", [], TenantA));

        await a.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        await Task.Delay(100); // any erroneous cross-tenant delivery would land in this window
        b.Received.Should().Be(0, $"tenant B must not receive tenant A's {resourceType} notification");
    }

    // A subscribe whose authorize ALLOW carries tenant B registers under tenant B (the allow's tenant), never
    // a caller-side tenant: a publish for B reaches the page, a publish for A does not. Tenant B differs from
    // the routing-isolation case's tenant A so a wrong sourcing cannot pass by accident.
    private static async Task AssertSubscribeKeySourcedFromAllowTenantAsync(SubscriptionResourceType resourceType)
    {
        var projectionName = ProjectionFor(resourceType);
        await using var dispatcher = NewDispatcher();
        var allowTenantB = StubSubscriptionAuthorizationClient.Allow(TenantB.Value);
        await using var subscription = new CircuitResourceSubscription(
            dispatcher, allowTenantB, new StubCircuitIdentity(Actor));

        var applied = new ConcurrentQueue<int>();
        var value = 0;
        await subscription.StartAsync(
            resourceType, "resource-1",
            _ => Task.FromResult(Interlocked.Increment(ref value)),
            v =>
            {
                applied.Enqueue(v);
                return Task.CompletedTask;
            },
            async render => await render(),
            CancellationToken.None);
        var afterSnapshot = applied.Count; // the snapshot applied once

        // Tenant B is the allow's tenant: a B-tenant notification reaches the subscriber.
        dispatcher.Publish(new NotificationEnvelope(projectionName, "resource-1", "Changed", [], TenantB));
        await WaitUntilAsync(() => applied.Count == afterSnapshot + 1, TimeSpan.FromSeconds(2));

        // Tenant A is NOT the allow's tenant: an A-tenant notification must not reach the B-keyed subscriber.
        dispatcher.Publish(new NotificationEnvelope(projectionName, "resource-1", "Changed", [], TenantA));
        await Task.Delay(100);
        applied.Count.Should().Be(
            afterSnapshot + 1,
            $"the {resourceType} subscriber must be keyed on the allow tenant, not the caller side");
    }

    private static string ProjectionFor(SubscriptionResourceType resourceType) =>
        ResourceRouting.ProjectionResourceTypes.First(entry => entry.Value == resourceType).Key;

    private static ResourceNotificationDispatcher NewDispatcher() =>
        new(new RecordingLogger<ResourceNotificationDispatcher>());

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Condition was not met within {timeout}.");
    }

    private sealed class Recorder
    {
        private int _count;

        public int Received => Volatile.Read(ref _count);

        public Func<NotificationEnvelope, Task> Handler => _ =>
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        };

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Volatile.Read(ref _count) >= count)
                {
                    return;
                }
                await Task.Delay(10);
            }
            throw new TimeoutException($"Expected at least {count} notification(s) within {timeout}.");
        }
    }

    private sealed class StubCircuitIdentity(Guid actorId) : ICircuitForwardedIdentityProvider
    {
        public Task<Guid> GetActorIdAsync() => Task.FromResult(actorId);
    }
}
