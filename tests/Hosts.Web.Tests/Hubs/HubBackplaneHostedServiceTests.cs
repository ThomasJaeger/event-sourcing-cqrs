using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Hubs;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

public class HubBackplaneHostedServiceTests
{
    [Fact]
    public Task Dispatch_order_detail_envelope_broadcasts_to_the_order_resource_group()
        => SubscriptionResourceCoverageTests.OrderDetailBroadcastCaseAsync();

    [Fact]
    public Task Dispatch_inventory_dashboard_envelope_broadcasts_to_the_inventory_resource_group()
        => SubscriptionResourceCoverageTests.InventoryDashboardBroadcastCaseAsync();

    [Fact]
    public async Task DispatchAsync_feeds_the_dispatcher_and_still_broadcasts_to_the_hub_group()
    {
        var hubContext = new RecordingHubContext();
        var dispatcher = new RecordingResourceNotificationDispatcher();
        var service = new HubBackplaneHostedService(
            new StubBackplane(), hubContext, NullLogger<HubBackplaneHostedService>.Instance, dispatcher);
        var envelope = new NotificationEnvelope(
            "order-detail", "order-7", "OrderShipped", ["status"], WellKnownTenants.Default);

        await service.DispatchAsync(envelope, CancellationToken.None);

        // Dual sink: the reader feeds the in-process dispatcher with the exact envelope AND still broadcasts
        // to the hub group (the broadcast stays live until the hub is retired in Commit 3).
        dispatcher.Published.Should().ContainSingle().Which.Should().Be(envelope);
        hubContext.Broadcasts.Should().ContainSingle()
            .Which.Group.Should().Be("tenant:00000000000000000000000000000001:order:order-7");
    }

    [Fact]
    public async Task DispatchAsync_broadcasts_to_the_tenant_qualified_group()
    {
        var hubContext = new RecordingHubContext();
        var service = Service(hubContext);
        // Inventory is the load-bearing family: the same SKU is legal under two tenants
        // (P10.6), so the group must qualify by the envelope's tenant or two tenants'
        // subscribers collide on inventory:SKU-1. A non-default tenant proves the segment
        // is the envelope's tenant, rendered in the StreamId {guid:N} form.
        var tenant = TenantId.From(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var envelope = new NotificationEnvelope(
            "inventory-dashboard", "SKU-1", "InventoryAdjusted", [], tenant);

        await service.DispatchAsync(envelope, CancellationToken.None);

        hubContext.Broadcasts[0].Group.Should().Be("tenant:55555555555555555555555555555555:inventory:SKU-1");
    }

    [Fact]
    public async Task Dispatch_broadcasts_only_to_the_one_resource_group()
    {
        var hubContext = new RecordingHubContext();
        var service = Service(hubContext);

        await service.DispatchAsync(
            new NotificationEnvelope("order-detail", "order-7", "OrderShipped", ["status"], WellKnownTenants.Default),
            CancellationToken.None);

        // Keyed on the resource id; exactly one broadcast, to that group and no other.
        hubContext.Broadcasts.Should().ContainSingle()
            .Which.Group.Should().Be("tenant:00000000000000000000000000000001:order:order-7");
    }

    [Fact]
    public async Task Dispatch_unmapped_projection_name_skips_and_logs_a_warning()
    {
        var hubContext = new RecordingHubContext();
        var logger = new RecordingLogger<HubBackplaneHostedService>();
        var service = new HubBackplaneHostedService(
            new StubBackplane(), hubContext, logger, new RecordingResourceNotificationDispatcher());

        await service.DispatchAsync(
            new NotificationEnvelope("customer-summary", "cust-1", "OrderPlaced", [], WellKnownTenants.Default),
            CancellationToken.None);

        hubContext.Broadcasts.Should().BeEmpty();
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("customer-summary"));
    }

    [Fact]
    public async Task ExecuteAsync_dispatches_through_the_loop_and_stops_cleanly_on_host_stop()
    {
        var hubContext = new RecordingHubContext();
        var envelope = new NotificationEnvelope("order-detail", "order-7", "OrderShipped", ["status"], WellKnownTenants.Default);
        var service = new HubBackplaneHostedService(
            new StubBackplane(envelope), hubContext, NullLogger<HubBackplaneHostedService>.Instance,
            new RecordingResourceNotificationDispatcher());

        await service.StartAsync(CancellationToken.None);
        // The seeded envelope flows through the loop before the backplane parks.
        await WaitUntilAsync(() => hubContext.Broadcasts.Count == 1, TimeSpan.FromSeconds(5));

        var stop = service.StopAsync(CancellationToken.None);
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(stop);
        await stop;
        hubContext.Broadcasts.Should().ContainSingle().Which.Group.Should().Be("tenant:00000000000000000000000000000001:order:order-7");
    }

    private static HubBackplaneHostedService Service(RecordingHubContext hubContext)
        => new(new StubBackplane(), hubContext, NullLogger<HubBackplaneHostedService>.Instance,
            new RecordingResourceNotificationDispatcher());

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
