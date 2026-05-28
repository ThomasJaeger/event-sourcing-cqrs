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
    public async Task Dispatch_order_detail_envelope_broadcasts_to_the_order_resource_group()
    {
        var hubContext = new RecordingHubContext();
        var service = Service(hubContext);
        var envelope = new NotificationEnvelope("order-detail", "order-7", "OrderShipped", ["status"]);

        await service.DispatchAsync(envelope, CancellationToken.None);

        hubContext.Broadcasts.Should().ContainSingle();
        var (group, method, args) = hubContext.Broadcasts[0];
        group.Should().Be("order:order-7");
        method.Should().Be(HubBackplaneHostedService.ClientMethod);
        args.Should().ContainSingle().Which.Should().Be(envelope);
    }

    [Fact]
    public async Task Dispatch_inventory_dashboard_envelope_broadcasts_to_the_inventory_resource_group()
    {
        var hubContext = new RecordingHubContext();
        var service = Service(hubContext);
        var envelope = new NotificationEnvelope("inventory-dashboard", "SKU-1", "InventoryAdjusted", ["on_hand"]);

        await service.DispatchAsync(envelope, CancellationToken.None);

        hubContext.Broadcasts.Should().ContainSingle();
        hubContext.Broadcasts[0].Group.Should().Be("inventory:SKU-1");
    }

    [Fact]
    public async Task Dispatch_broadcasts_only_to_the_one_resource_group()
    {
        var hubContext = new RecordingHubContext();
        var service = Service(hubContext);

        await service.DispatchAsync(
            new NotificationEnvelope("order-detail", "order-7", "OrderShipped", ["status"]),
            CancellationToken.None);

        // Keyed on the resource id; exactly one broadcast, to that group and no other.
        hubContext.Broadcasts.Should().ContainSingle()
            .Which.Group.Should().Be("order:order-7");
    }

    [Fact]
    public async Task Dispatch_unmapped_projection_name_skips_and_logs_a_warning()
    {
        var hubContext = new RecordingHubContext();
        var logger = new RecordingLogger<HubBackplaneHostedService>();
        var service = new HubBackplaneHostedService(new StubBackplane(), hubContext, logger);

        await service.DispatchAsync(
            new NotificationEnvelope("customer-summary", "cust-1", "OrderPlaced", []),
            CancellationToken.None);

        hubContext.Broadcasts.Should().BeEmpty();
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("customer-summary"));
    }

    [Fact]
    public async Task ExecuteAsync_dispatches_through_the_loop_and_stops_cleanly_on_host_stop()
    {
        var hubContext = new RecordingHubContext();
        var envelope = new NotificationEnvelope("order-detail", "order-7", "OrderShipped", ["status"]);
        var service = new HubBackplaneHostedService(
            new StubBackplane(envelope), hubContext, NullLogger<HubBackplaneHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        // The seeded envelope flows through the loop before the backplane parks.
        await WaitUntilAsync(() => hubContext.Broadcasts.Count == 1, TimeSpan.FromSeconds(5));

        var stop = service.StopAsync(CancellationToken.None);
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(stop);
        await stop;
        hubContext.Broadcasts.Should().ContainSingle().Which.Group.Should().Be("order:order-7");
    }

    private static HubBackplaneHostedService Service(RecordingHubContext hubContext)
        => new(new StubBackplane(), hubContext, NullLogger<HubBackplaneHostedService>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
