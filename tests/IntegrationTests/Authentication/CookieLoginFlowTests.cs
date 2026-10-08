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
    public async Task The_framework_script_required_by_the_workspace_is_served()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        using var response = await client.GetAsync("/_framework/blazor.web.js");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Blazor");
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Contain("javascript");
    }

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

    [Fact]
    public async Task Account_get_is_static_and_does_not_start_an_interactive_circuit()
    {
        await using var factory = new WebHostFactory();
        using var client = AccountClient(factory);
        using var response = await client.GetAsync("/login?ReturnUrl=%2Forders");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("name=\"password\"").And.Contain("value=\"/orders\"")
            .And.NotContain("blazor.web.js").And.NotContain("<!--Blazor:");
        html.Should().Contain("data-enhance-nav=\"false\"");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        TokenFromHtml(html).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_stale_anonymous_login_token_returns_the_current_signed_in_account_without_signing_in_again()
    {
        await using var factory = new WebHostFactory();
        using var client = AccountClient(factory);
        var oldToken = await GetAntiforgeryTokenAsync(client);
        using var signedIn = await PostLoginAsync(client, oldToken);
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var rejected = await PostLoginAsync(client, oldToken, "https://untrusted.invalid/stale");

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNoAuthCookie(rejected);
        rejected.Headers.Location.Should().BeNull();
        var html = await rejected.Content.ReadAsStringAsync();
        html.Should().Contain(ExpiredFormMessage).And.Contain("Signed in as")
            .And.Contain(ConfiguredActor.ToString()).And.Contain("Continue to orders")
            .And.Contain("/account/logout").And.NotContain("untrusted.invalid");
        rejected.Headers.CacheControl.Should().NotBeNull();
        rejected.Headers.CacheControl!.NoStore.Should().BeTrue();
        var freshToken = TokenFromHtml(html);
        freshToken.Should().NotBe(oldToken);
        using var signedOut = await client.PostAsync("/account/logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = freshToken }));
        signedOut.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task A_stale_logout_token_returns_a_fresh_sign_in_form_that_can_be_submitted()
    {
        await using var factory = new WebHostFactory();
        using var client = AccountClient(factory);
        using var signedIn = await PostLoginAsync(client, await GetAntiforgeryTokenAsync(client));
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var oldToken = await GetAntiforgeryTokenAsync(client);
        using var signedOut = await client.PostAsync("/account/logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = oldToken }));
        signedOut.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var rejected = await client.PostAsync("/account/logout", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = oldToken,
                ["returnUrl"] = "https://untrusted.invalid/stale",
            }));

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNoAuthCookie(rejected);
        var html = await rejected.Content.ReadAsStringAsync();
        html.Should().Contain(ExpiredFormMessage).And.Contain("name=\"password\"")
            .And.NotContain("Signed in as").And.NotContain("untrusted.invalid");
        rejected.Headers.CacheControl.Should().NotBeNull();
        rejected.Headers.CacheControl!.NoStore.Should().BeTrue();
        var freshToken = TokenFromHtml(html);
        freshToken.Should().NotBe(oldToken);
        using var retried = await PostLoginAsync(client, freshToken);
        retried.StatusCode.Should().Be(HttpStatusCode.Redirect);
        retried.Headers.Location!.OriginalString.Should().Be("/orders");
        retried.Headers.GetValues("Set-Cookie")
            .Should().Contain(value => value.StartsWith(".EventSourcingCqrs.Web.Auth="));
    }

    [Theory]
    [InlineData("https://untrusted.invalid/outside")]
    [InlineData("//untrusted.invalid/outside")]
    public async Task An_external_return_url_is_rejected_before_any_auth_cookie_is_issued(string returnUrl)
    {
        await using var factory = new WebHostFactory();
        using var client = AccountClient(factory);
        using var response = await PostLoginAsync(client, await GetAntiforgeryTokenAsync(client), returnUrl);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Location.Should().BeNull();
        AssertNoAuthCookie(response);
    }

    private const string ExpiredFormMessage =
        "This form expired or could not be verified. Please use the current account form below.";

    private static HttpClient AccountClient(WebHostFactory factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string token,
        string returnUrl = "/orders") => client.PostAsync("/account/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["password"] = Password,
                ["returnUrl"] = returnUrl,
            }));

    private static void AssertNoAuthCookie(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            cookies.Should().NotContain(value => value.StartsWith(".EventSourcingCqrs.Web.Auth="));
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        return TokenFromHtml(await client.GetStringAsync("/login"));
    }

    private static string TokenFromHtml(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("the login page renders an antiforgery token");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public sealed class WebHostFactory : WebApplicationFactory<WebHost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            // These factories run build output, not a publish directory. Load the build asset manifest.
            builder.UseStaticWebAssets();
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
