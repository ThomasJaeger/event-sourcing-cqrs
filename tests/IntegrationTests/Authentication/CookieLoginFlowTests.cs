extern alias WebHost;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
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

    private const string Password = "A test-only operator password!";

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
            ["password"] = Password,
        });

        var response = await client.PostAsync("/account/login", form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/");
        response.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue();
        setCookies!.Should().Contain(value => value.StartsWith(".EventSourcingCqrs.Web.Auth"));

        var signedInPage = await client.GetStringAsync("/login");
        signedInPage.Should().Contain("Signed in as");
        signedInPage.Should().Contain(ConfiguredActor.ToString());
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("incorrect password")]
    public async Task A_login_without_the_correct_password_does_not_issue_an_auth_cookie(string? password)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await GetAntiforgeryTokenAsync(client),
            ["returnUrl"] = "/",
        };
        if (password is not null) fields["password"] = password;

        using var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            cookies.Should().NotContain(value => value.StartsWith(".EventSourcingCqrs.Web.Auth"));
    }

    [Fact]
    public async Task Repeated_login_attempts_are_rate_limited()
    {
        await using var factory = new WebHostFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        var token = await GetAntiforgeryTokenAsync(client);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var denied = await client.PostAsync("/account/login", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["password"] = "wrong" }));
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var limited = await client.PostAsync("/account/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["password"] = "wrong" }));

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_cookie_issued_by_the_legacy_passwordless_scheme_is_rejected()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        // Use the same key ring as the upgraded host: only the authentication purpose changes.
        var protector = _factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(
            "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware",
            CookieAuthenticationDefaults.AuthenticationScheme, "v2");
        var legacyPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, ConfiguredActor.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme));
        var ticket = new AuthenticationTicket(legacyPrincipal, new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        var cookie = new TicketDataFormat(protector).Protect(ticket);
        client.DefaultRequestHeaders.Add("Cookie", ".EventSourcingCqrs.Web.Auth=" + cookie);

        var html = await client.GetStringAsync("/login");

        html.Should().Contain("name=\"password\"");
        html.Should().NotContain("Signed in as");
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
            builder.UseEnvironment("Production");
            builder.UseSetting("OperatorAuthentication:PasswordHash", new PasswordHasher<string>().HashPassword("operator", Password));
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
