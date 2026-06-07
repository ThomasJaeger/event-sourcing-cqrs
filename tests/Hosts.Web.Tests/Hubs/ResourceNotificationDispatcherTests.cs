using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Hubs;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

// Commit 1, ADR 0032 (superseding ADR 0027). The in-process dispatcher replaces the SignalR hub
// broadcast: the single backplane reader publishes each projection notification here, and the dispatcher
// fans it out to the circuit-scoped subscribers registered for that resource. These specs pin the routing
// (per-resource, tenant-isolated, driven from the enumerable routing map), the fan-out, and the resilience
// invariants the audit fixed (fault isolation, snapshot-on-publish, bounded coalescing queues, clean
// deregistration) before any fan-out logic exists.
public class ResourceNotificationDispatcherTests
{
    private static readonly TenantId TenantA = TenantId.From(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly TenantId TenantB = TenantId.From(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    private static NotificationEnvelope OrderEnvelope(string orderId, TenantId tenant) =>
        new("order-detail", orderId, "OrderShipped", ["status"], tenant);

    private static NotificationEnvelope InventoryEnvelope(string sku, TenantId tenant) =>
        new("inventory-dashboard", sku, "InventoryAdjusted", ["on_hand"], tenant);

    private static ResourceNotificationDispatcher NewDispatcher() =>
        new(new RecordingLogger<ResourceNotificationDispatcher>());

    [Fact]
    public async Task Publish_delivers_to_a_subscriber_registered_for_the_resource_key()
    {
        await using var dispatcher = NewDispatcher();
        var key = new ResourceKey(TenantA, SubscriptionResourceType.Order, "order-1");
        var collector = new Collector();
        using var registration = dispatcher.Subscribe(key, collector.Handler);

        var envelope = OrderEnvelope("order-1", TenantA);
        dispatcher.Publish(envelope);

        await collector.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        collector.Received.Should().ContainSingle().Which.Should().Be(envelope);
    }

    [Fact]
    public async Task Two_subscribers_for_the_same_resource_both_receive()
    {
        await using var dispatcher = NewDispatcher();
        var key = new ResourceKey(TenantA, SubscriptionResourceType.Inventory, "SKU-1");
        var a = new Collector();
        var b = new Collector();
        using var registrationA = dispatcher.Subscribe(key, a.Handler);
        using var registrationB = dispatcher.Subscribe(key, b.Handler);

        var envelope = InventoryEnvelope("SKU-1", TenantA);
        dispatcher.Publish(envelope);

        await a.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        await b.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        a.Received.Should().ContainSingle().Which.Should().Be(envelope);
        b.Received.Should().ContainSingle().Which.Should().Be(envelope);
    }

    public static IEnumerable<object[]> RoutedResourceTypes() =>
        ResourceRouting.ProjectionResourceTypes
            .Select(entry => new object[] { entry.Key, entry.Value });

    [Theory]
    [MemberData(nameof(RoutedResourceTypes))]
    public async Task A_notification_for_one_tenant_does_not_reach_another_tenants_subscriber(
        string projectionName, SubscriptionResourceType resourceType)
    {
        // Per routed resource type rather than Inventory alone. A and B subscribe to the same resource
        // under different tenants, A's tenant publishes, and B must stay empty. Driven from the routing
        // map so every type a page can subscribe to is covered, with the per-type teeth the hub held.
        await using var dispatcher = NewDispatcher();
        var a = new Collector();
        var b = new Collector();
        using var registrationA = dispatcher.Subscribe(
            new ResourceKey(TenantA, resourceType, "resource-1"), a.Handler);
        using var registrationB = dispatcher.Subscribe(
            new ResourceKey(TenantB, resourceType, "resource-1"), b.Handler);
        dispatcher.Publish(new NotificationEnvelope(projectionName, "resource-1", "Changed", [], TenantA));
        await a.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        await Task.Delay(100); // any erroneous cross-tenant delivery would land in this window
        b.Received.Should().BeEmpty();
    }

    [Fact]
    public void An_unmapped_projection_is_logged_and_dropped_without_throwing()
    {
        var logger = new RecordingLogger<ResourceNotificationDispatcher>();
        var dispatcher = new ResourceNotificationDispatcher(logger);

        var act = () => dispatcher.Publish(
            new NotificationEnvelope("unmapped-projection", "id-1", "Changed", [], TenantA));

        act.Should().NotThrow();
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("unmapped-projection"));
    }

    [Fact]
    public async Task Every_routing_map_entry_round_trips_from_publish_to_a_subscriber_on_the_derived_key()
    {
        // Enumerates the routing map itself, not a hand-list, so a projection added to the map without a
        // working route fails here. Closes the private-static GroupPrefixes blind spot.
        foreach (var (projectionName, resourceType) in ResourceRouting.ProjectionResourceTypes)
        {
            await using var dispatcher = NewDispatcher();
            var key = new ResourceKey(TenantA, resourceType, "round-trip-id");
            var collector = new Collector();
            using var registration = dispatcher.Subscribe(key, collector.Handler);

            dispatcher.Publish(new NotificationEnvelope(projectionName, "round-trip-id", "Changed", [], TenantA));

            await collector.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
            collector.Received.Should().ContainSingle()
                .Which.ProjectionName.Should().Be(projectionName);
        }
    }

    [Fact]
    public void Every_subscription_resource_type_has_a_publishing_projection_in_the_routing_map()
    {
        // The set-to-entries direction of enumerability: a resource type a page can subscribe to but that no
        // projection routes to would deliver nothing. The routing map is the single source, so this gates a
        // SubscriptionResourceType added without a publishing projection. The full subscribe-and-broadcast
        // coverage harness re-points onto the dispatcher in Commit 3; this guards the routing half now.
        var routedTypes = ResourceRouting.ProjectionResourceTypes.Values.Distinct().ToArray();

        Enum.GetValues<SubscriptionResourceType>().Should().BeSubsetOf(routedTypes);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_stop_delivery_to_others_or_terminate_the_dispatcher()
    {
        var logger = new RecordingLogger<ResourceNotificationDispatcher>();
        await using var dispatcher = new ResourceNotificationDispatcher(logger);
        var key = new ResourceKey(TenantA, SubscriptionResourceType.Order, "order-1");
        var faulting = new Collector(_ => throw new InvalidOperationException("subscriber boom"));
        var healthy = new Collector();
        using var registrationF = dispatcher.Subscribe(key, faulting.Handler);
        using var registrationH = dispatcher.Subscribe(key, healthy.Handler);

        dispatcher.Publish(OrderEnvelope("order-1", TenantA));
        await healthy.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        // The dispatcher survives the fault: a second notification still reaches the healthy subscriber.
        dispatcher.Publish(OrderEnvelope("order-1", TenantA));
        await healthy.WaitForCountAsync(2, TimeSpan.FromSeconds(2));
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task A_subscriber_added_or_removed_during_publishing_never_makes_publish_throw()
    {
        await using var dispatcher = NewDispatcher();
        var key = new ResourceKey(TenantA, SubscriptionResourceType.Order, "order-1");
        var stable = new Collector();
        using var stableRegistration = dispatcher.Subscribe(key, stable.Handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var churn = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var registration = dispatcher.Subscribe(key, _ => Task.CompletedTask);
                registration.Dispose();
            }
        });

        Exception? publishFault = null;
        try
        {
            for (var i = 0; i < 200 && !cts.IsCancellationRequested; i++)
            {
                dispatcher.Publish(OrderEnvelope("order-1", TenantA));
            }
        }
        catch (Exception ex)
        {
            publishFault = ex;
        }

        await cts.CancelAsync();
        await churn;

        publishFault.Should().BeNull();
        await stable.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Rapid_notifications_to_one_resource_coalesce_to_the_latest()
    {
        await using var dispatcher = NewDispatcher();
        var key = new ResourceKey(TenantA, SubscriptionResourceType.Order, "order-1");
        var firstSeen = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var collector = new Collector(env =>
        {
            // Block while the first notification is in flight so the next two queue behind it; the bounded
            // queue must coalesce the middle one away and deliver only the latest.
            if (env.EventName == "first")
            {
                firstSeen.SetResult();
                return release.Task;
            }
            return Task.CompletedTask;
        });
        using var registration = dispatcher.Subscribe(key, collector.Handler);

        dispatcher.Publish(new NotificationEnvelope("order-detail", "order-1", "first", ["status"], TenantA));
        await firstSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dispatcher.Publish(new NotificationEnvelope("order-detail", "order-1", "middle", ["status"], TenantA));
        dispatcher.Publish(new NotificationEnvelope("order-detail", "order-1", "last", ["status"], TenantA));
        release.SetResult();

        await collector.WaitForCountAsync(2, TimeSpan.FromSeconds(2));
        await Task.Delay(100); // an uncoalesced third delivery, if it wrongly happened, would land here
        collector.Received.Select(e => e.EventName).Should().Equal("first", "last");
    }

    [Fact]
    public async Task A_disposed_registration_receives_no_further_notifications()
    {
        await using var dispatcher = NewDispatcher();
        var key = new ResourceKey(TenantA, SubscriptionResourceType.Order, "order-1");
        var collector = new Collector();
        var registration = dispatcher.Subscribe(key, collector.Handler);

        dispatcher.Publish(OrderEnvelope("order-1", TenantA));
        await collector.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        registration.Dispose();
        dispatcher.Publish(OrderEnvelope("order-1", TenantA));
        await Task.Delay(150); // a delivery, if it wrongly still happened, would land in this window
        collector.Received.Should().ContainSingle();
    }

    // Records every delivered envelope and, optionally, runs a hook so a test can block or fault inside the
    // subscriber. WaitForCountAsync polls because delivery happens off the publish thread on the drain loop.
    private sealed class Collector
    {
        private readonly object _gate = new();
        private readonly List<NotificationEnvelope> _received = [];
        private readonly Func<NotificationEnvelope, Task>? _hook;

        public Collector(Func<NotificationEnvelope, Task>? hook = null) => _hook = hook;

        public Func<NotificationEnvelope, Task> Handler => async env =>
        {
            lock (_gate)
            {
                _received.Add(env);
            }
            if (_hook is not null)
            {
                await _hook(env);
            }
        };

        public IReadOnlyList<NotificationEnvelope> Received
        {
            get
            {
                lock (_gate)
                {
                    return _received.ToArray();
                }
            }
        }

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_received.Count >= count)
                    {
                        return;
                    }
                }
                await Task.Delay(10);
            }
            throw new TimeoutException(
                $"Expected at least {count} notification(s) within {timeout}, but saw {Received.Count}.");
        }
    }
}
