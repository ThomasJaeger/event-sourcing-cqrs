extern alias WebHost;
using System;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using WebHost::EventSourcingCqrs.Hosts.Web.Hubs;

namespace EventSourcingCqrs.IntegrationTests.SignalR;

// The Web host's in-process notification composition (ADR 0032). The dispatcher is a singleton (one
// in-process fan-out for the whole host, so a change reaches every circuit/tab) and the circuit subscription
// is transient (one per page instance, since the page owns and disposes it). Asserted by resolving from the
// host's provider through a WebApplicationFactory boot. This is a composition choice, not a framework
// guarantee: mis-registering either lifetime is a real defect.
public class ResourceNotificationCompositionTests
    : IClassFixture<ResourceNotificationCompositionTests.WebHostFactory>
{
    private readonly WebHostFactory _factory;

    public ResourceNotificationCompositionTests(WebHostFactory factory) => _factory = factory;

    // Async scopes: the circuit subscription is IAsyncDisposable-only, so a synchronous scope dispose throws.
    [Fact]
    public async Task The_resource_notification_dispatcher_is_a_singleton_shared_across_circuits()
    {
        await using var circuitA = _factory.Services.CreateAsyncScope();
        await using var circuitB = _factory.Services.CreateAsyncScope();

        var a = circuitA.ServiceProvider.GetRequiredService<IResourceNotificationDispatcher>();
        var b = circuitB.ServiceProvider.GetRequiredService<IResourceNotificationDispatcher>();

        a.Should().BeSameAs(b);
    }

    [Fact]
    public async Task The_circuit_resource_subscription_is_a_distinct_instance_per_resolution()
    {
        await using var circuit = _factory.Services.CreateAsyncScope();

        var first = circuit.ServiceProvider.GetRequiredService<ICircuitResourceSubscription>();
        var second = circuit.ServiceProvider.GetRequiredService<ICircuitResourceSubscription>();

        // The page owns one subscription per page instance, so two resolutions in the same circuit must not
        // be the same object (transient, not scoped). Scoped hands a second same-circuit navigation the
        // instance the first page already disposed in its DisposeAsync.
        first.Should().NotBeSameAs(second);
    }

    public sealed class WebHostFactory : WebApplicationFactory<WebHost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("API_BASE_URL", "https://api.localhost");
            builder.UseSetting(
                "FORWARDED_IDENTITY_SIGNING_SECRET",
                "resource-notification-composition-tests-forwarded-identity-secret");
            builder.UseSetting("BootstrapAdministrator:AdministratorUserId", Guid.NewGuid().ToString());
            builder.UseSetting(
                "READ_MODEL_CONNECTION_STRING", "Host=localhost;Database=unused;Username=u;Password=p");

            // Remove the Postgres-backed backplane hosted service so the host boots without a database; this
            // test inspects the service graph, not the dashboards.
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
