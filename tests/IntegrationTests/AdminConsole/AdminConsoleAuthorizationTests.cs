extern alias AdminConsoleHost;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// ADR 0040: the AdminConsole fails closed. An unauthenticated request to a gated route must be
// challenged by the cookie scheme with a redirect to the static login path. Cookie issuance is
// covered separately by AdminConsoleLoginFlowTests.
public class AdminConsoleAuthorizationTests
{
    [Fact]
    public async Task The_account_stylesheet_is_public_without_opening_operator_routes()
    {
        using var factory = new WebApplicationFactory<AdminConsoleHost::Program>()
            .WithWebHostBuilder(builder => builder
                .WithOperatorCredentials()
                .UseSetting("READ_MODEL_CONNECTION_STRING", "Host=localhost;Database=unused;Username=u;Password=p")
                .UseSetting("EVENT_STORE_CONNECTION_STRING", "Host=localhost;Database=unused;Username=u;Password=p"));
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/console.css");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/css");
        (await response.Content.ReadAsStringAsync()).Should().Contain(".console-shell");
        using var tool = await client.GetAsync("/audit");
        tool.StatusCode.Should().Be(HttpStatusCode.Found);
        tool.Headers.Location!.AbsolutePath.Should().Be("/login");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/audit")]
    [InlineData("/order-history")]
    public async Task Unauthenticated_request_to_a_gated_route_is_redirected_to_the_login_path(string path)
    {
        using var factory = new WebApplicationFactory<AdminConsoleHost::Program>()
            .WithWebHostBuilder(builder => builder
                .WithOperatorCredentials()
                .UseSetting("READ_MODEL_CONNECTION_STRING", "Host=localhost;Database=unused;Username=u;Password=p")
                .UseSetting("EVENT_STORE_CONNECTION_STRING", "Host=localhost;Database=unused;Username=u;Password=p"));
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.AbsolutePath.Should().Be("/login");
        response.Headers.Location!.Query.Should().Contain("ReturnUrl");
    }
}
