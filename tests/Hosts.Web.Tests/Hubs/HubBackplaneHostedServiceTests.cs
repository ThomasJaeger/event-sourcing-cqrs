using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web.Hubs;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

// The backplane reader after the SignalR hub retired (ADR 0032): it feeds each notification to the
// in-process dispatcher and holds no transport detail. Routing and the unmapped-projection drop moved to
// the dispatcher (covered by ResourceNotificationDispatcherTests), so these specs assert only the feed and
// the loop lifecycle.
public class HubBackplaneHostedServiceTests
{
    [Fact]
    public void Dispatch_feeds_the_dispatcher_with_the_envelope()
    {
        var dispatcher = new RecordingResourceNotificationDispatcher();
        var service = new HubBackplaneHostedService(new StubBackplane(), dispatcher);
        var envelope = new NotificationEnvelope(
            "order-detail", "order-7", "OrderShipped", ["status"], WellKnownTenants.Default);

        service.Dispatch(envelope);

        dispatcher.Published.Should().ContainSingle().Which.Should().Be(envelope);
    }

    [Fact]
    public async Task ExecuteAsync_feeds_the_dispatcher_through_the_loop_and_stops_cleanly_on_host_stop()
    {
        var dispatcher = new RecordingResourceNotificationDispatcher();
        var envelope = new NotificationEnvelope(
            "order-detail", "order-7", "OrderShipped", ["status"], WellKnownTenants.Default);
        var service = new HubBackplaneHostedService(new StubBackplane(envelope), dispatcher);

        await service.StartAsync(CancellationToken.None);
        // The seeded envelope flows through the loop before the backplane parks.
        await WaitUntilAsync(() => dispatcher.Published.Count == 1, TimeSpan.FromSeconds(5));

        var stop = service.StopAsync(CancellationToken.None);
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(stop);
        await stop;
        dispatcher.Published.Should().ContainSingle().Which.Should().Be(envelope);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
