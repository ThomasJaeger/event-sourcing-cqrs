extern alias WebHost;
using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Authentication;

// Proves the dashboard hub endpoint carries an authorization requirement (P9.6), so an unauthenticated
// connection is refused at negotiate. Asserted by composition through the Web-host WebApplicationFactory
// rather than by driving a live HubConnection: a Blazor Server circuit is a browser SignalR connection
// not headlessly drivable through WebApplicationFactory (see CookieLoginFlowTests), so the route
// requirement is proven by inspecting the endpoint metadata. The factory removes the Postgres-backed
// backplane hosted service so the host boots without a database.
public class DashboardHubRouteTests : IClassFixture<DashboardHubRouteTests.WebHostFactory>
{
    private readonly WebHostFactory _factory;

    public DashboardHubRouteTests(WebHostFactory factory) => _factory = factory;

    [Fact]
    public void The_dashboard_hub_endpoint_requires_authorization()
    {
        var hubEndpoints = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is not null
                && e.RoutePattern.RawText.StartsWith("/hubs/dashboard", StringComparison.Ordinal))
            .ToList();

        hubEndpoints.Should().NotBeEmpty("MapHub registers the dashboard hub endpoints");
        hubEndpoints.Should().OnlyContain(
            e => e.Metadata.GetMetadata<IAuthorizeData>() != null,
            "the hub route requires authorization so an unauthenticated connection is refused");
    }

    public sealed class WebHostFactory : WebApplicationFactory<WebHost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("API_BASE_URL", "https://api.localhost");
            builder.UseSetting(
                "FORWARDED_IDENTITY_SIGNING_SECRET", "dashboard-hub-route-tests-forwarded-identity-secret");
            builder.UseSetting("BootstrapAdministrator:AdministratorUserId", Guid.NewGuid().ToString());
            builder.UseSetting(
                "READ_MODEL_CONNECTION_STRING", "Host=localhost;Database=unused;Username=u;Password=p");

            // Remove the Postgres-backed backplane hosted service so the host boots without a database;
            // this test inspects the endpoint graph, not the dashboards.
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
