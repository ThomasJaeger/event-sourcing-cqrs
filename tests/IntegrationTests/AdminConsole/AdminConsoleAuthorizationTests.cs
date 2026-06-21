extern alias AdminConsoleHost;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// ADR 0040: the AdminConsole fails closed. An unauthenticated request to a gated route must be
// challenged by the cookie scheme with a redirect to the login path. The interactive login page is a
// later consumer, so the path resolves to nothing yet; the redirect itself is the fail-closed proof.
public class AdminConsoleAuthorizationTests
{
    [Fact]
    public async Task Unauthenticated_request_to_a_gated_route_is_redirected_to_the_login_path()
    {
        using var factory = new WebApplicationFactory<AdminConsoleHost::Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "READ_MODEL_CONNECTION_STRING", "Host=localhost;Database=unused;Username=u;Password=p"));
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.AbsolutePath.Should().Be("/login");
        response.Headers.Location!.Query.Should().Contain("ReturnUrl");
    }
}
