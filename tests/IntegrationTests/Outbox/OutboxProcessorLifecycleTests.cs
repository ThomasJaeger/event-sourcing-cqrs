using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Outbox;

// What this pins: a hosted service's shutdown is re-entrant safe. The framework's disposal path
// enters IHostedService.StopAsync more than once, and a second entry must no-op rather than throw.
// OutboxProcessor.StopAsync tears down the LISTEN/NOTIFY listener by checking _listenerCts for null,
// awaiting the cancel and the listener task, then disposing the field and nulling it. The check and
// the act are separated by two awaits, so a second entry that arrives while the first is parked walks
// past the null check and dereferences a field the first entry has already nulled.
//
// A shutdown that throws is not cosmetic. It aborts the rest of the host's teardown, so the listener
// connection and the batch transaction below it are left to the finalizer instead of being closed,
// and an operator restarting the Workers host sees an unhandled NullReferenceException on the way
// out. Chapter 8's outbox is only as good as its shutdown.
//
// The test boots the processor in a composed host, with no workload at all, and disposes it the way
// the framework does. No orders, no settle, no read-model assertion: the defect is in the lifecycle,
// not in the draining, and this reproduces it in well under a second.
//
// The registration is the same scoped WithWebHostBuilder overlay the exit-condition test uses: the
// Api host does not drain the outbox in production (src/Hosts/Api/Program.cs:53-56), so the processor
// is added to this class's host instance only and the shared ApiFixture default stays untouched.
public sealed class OutboxProcessorLifecycleTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public OutboxProcessorLifecycleTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Disposing_a_host_that_runs_the_outbox_processor_does_not_throw()
    {
        var factory = _fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddPostgresOutboxProcessor()));

        // Hosted services start with the server, and the server starts on the first client. Without
        // this the processor never runs StartAsync, so there is no listener to tear down and the
        // shutdown path under test is never entered.
        _ = factory.CreateClient();

        var disposingTheHost = async () => await factory.DisposeAsync();

        await disposingTheHost.Should().NotThrowAsync(
            "a hosted service entered through the framework's disposal path must shut down cleanly");
    }
}
