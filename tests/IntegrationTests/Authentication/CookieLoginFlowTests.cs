extern alias WebHost;
using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Authentication;

// Proves OUR Web wiring's login-to-cookie half: the real /login page plus /account/login endpoint,
// given a valid antiforgery token, sign in the configured actor and issue the auth cookie; a post
// without the token is rejected. The cookie-to-circuit seeding is proven separately by the spike on
// .NET 10 and durably by CircuitForwardedIdentityProviderTests; the live circuit -> ApiClient -> Api
// leg is not headlessly drivable through WebApplicationFactory (a Blazor Server circuit is a browser
// SignalR connection), so it is covered by composition (this test + the signer-acceptance test + the
// provider test + the spike), not overclaimed here. The factory removes the Postgres-backed SignalR
// backplane hosted service so the host boots without a database.
public class CookieLoginFlowTests : IClassFixture<CookieLoginFlowTests.WebHostFactory>
{
    private static readonly Guid ConfiguredActor = Guid.Parse("0a9f7c2e-4d6b-4c1a-9f3e-2b8d5e7a1c40");

    private readonly WebHostFactory _factory;

    public CookieLoginFlowTests(WebHostFactory factory) => _factory = factory;

    [Fact]
    public async Task A_valid_login_post_issues_the_auth_cookie()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });

        var token = await GetAntiforgeryTokenAsync(client);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["returnUrl"] = "/",
        });

        var response = await client.PostAsync("/account/login", form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/");
        response.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue();
        setCookies!.Should().Contain(value => value.StartsWith(".EventSourcingCqrs.Web.Auth"));
    }

    [Fact]
    public async Task A_login_post_without_an_antiforgery_token_is_rejected()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsync(
            "/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["returnUrl"] = "/" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/login");
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("the login page renders an antiforgery token");
        return match.Groups[1].Value;
    }

    public sealed class WebHostFactory : WebApplicationFactory<WebHost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("API_BASE_URL", "https://api.localhost");
            builder.UseSetting(
                "FORWARDED_IDENTITY_SIGNING_SECRET", "cookie-login-flow-forwarded-identity-secret");
            builder.UseSetting("BootstrapAdministrator:AdministratorUserId", ConfiguredActor.ToString());
            builder.UseSetting(
                "READ_MODEL_CONNECTION_STRING", "Host=localhost;Database=unused;Username=u;Password=p");

            // Remove the Postgres-backed SignalR backplane hosted service so the host boots without a
            // database; this test exercises only the cookie login surface, not the dashboards.
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
