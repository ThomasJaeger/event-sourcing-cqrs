extern alias AdminConsoleHost;
using System.Net;
using System.Text.RegularExpressions;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.ReadModels;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// Real cookie issuance and HTTP authorization, with only our current-roles port replaced. No test
// authentication scheme: the browser must prove the configured password before any tool is admitted.
public class AdminConsoleLoginFlowTests
{
    private const string Password = "An admin test-only password!";
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Anonymous_login_is_a_static_password_form_with_an_antiforgery_token()
    {
        await using var factory = new LoginFactory();
        using var client = Client(factory);
        using var response = await client.GetAsync("/login?ReturnUrl=%2Fstreams");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("name=\"password\"").And.Contain("/account/login")
            .And.Contain("value=\"/streams\"")
            .And.NotContain("blazor.web.js").And.NotContain("<!--Blazor:");
        Token(html).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Correct_password_issues_a_separate_secure_cookie_for_the_configured_actor()
    {
        await using var factory = new LoginFactory();
        using var client = Client(factory);
        var response = await LoginAsync(client, "/streams");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/streams");
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(".EventSourcingCqrs.AdminConsole.Auth="));
        cookie.Should().Contain("secure").And.Contain("httponly").And.Contain("samesite=lax");
        response.Headers.GetValues("Set-Cookie")
            .Should().NotContain(value => value.StartsWith(".EventSourcingCqrs.Web.Auth="));
        var account = await client.GetStringAsync("/login");
        account.Should().Contain(Actor.ToString()).And.Contain("/account/logout");
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Signed_in_admin_can_load_the_framework_script_needed_for_interactive_tools()
    {
        await using var factory = new LoginFactory();
        using var client = Client(factory);
        using var signedIn = await LoginAsync(client);
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var response = await client.GetAsync("/_framework/blazor.web.js");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Blazor");
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Contain("javascript");
    }

    [Theory]
    [InlineData("")]
    [InlineData("incorrect password")]
    public async Task Incorrect_password_issues_no_cookie_and_keeps_a_usable_login_form(string password)
    {
        await using var factory = new LoginFactory();
        using var client = Client(factory);
        var fields = await FieldsAsync(client);
        fields["password"] = password;
        using var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("name=\"password\"")
            .And.Contain("password was not accepted");
        AssertNoAuthCookie(response);
    }

    [Theory]
    [InlineData("/account/login")]
    [InlineData("/account/logout")]
    public async Task Account_posts_without_a_valid_antiforgery_token_are_rejected(string path)
    {
        await using var factory = new LoginFactory();
        using var client = Client(factory);
        using var response = await client.PostAsync(path,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = Password }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNoAuthCookie(response);
    }

    [Fact]
    public async Task A_stale_login_form_preserves_the_session_and_offers_a_current_logout_form()
    {
        await using var factory = new LoginFactory();
        using var client = Client(factory);
        var fields = await FieldsAsync(client);
        using var signedIn = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields));
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var stale = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields));

        stale.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNoAuthCookie(stale);
        var html = await stale.Content.ReadAsStringAsync();
        html.Should().Contain("role=\"alert\"").And.Contain("form expired")
            .And.Contain("Signed in as").And.Contain("Open operations console")
            .And.NotContain("name=\"password\"").And.NotContain("<!--Blazor:");
        stale.Headers.CacheControl.Should().NotBeNull();
        stale.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var signedOut = await client.PostAsync("/account/logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(html) }));
        signedOut.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task A_stale_logout_form_offers_a_new_login_without_replaying_the_old_action()
    {
        await using var factory = new LoginFactory();
        using var client = Client(factory);
        using var signedIn = await LoginAsync(client);
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/login")),
        };
        using var signedOut = await client.PostAsync("/account/logout", new FormUrlEncodedContent(fields));
        signedOut.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var stale = await client.PostAsync("/account/logout", new FormUrlEncodedContent(fields));

        stale.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNoAuthCookie(stale);
        var html = await stale.Content.ReadAsStringAsync();
        html.Should().Contain("role=\"alert\"").And.Contain("form expired")
            .And.Contain("name=\"password\"").And.NotContain("Signed in as");
        using var recovered = await client.PostAsync("/account/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = Token(html),
                ["password"] = Password,
            }));
        recovered.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("https://example.com/outside")]
    [InlineData("//example.com/outside")]
    public async Task A_nonlocal_return_url_is_rejected_before_cookie_issuance(string returnUrl)
    {
        await using var factory = new LoginFactory();
        using var client = Client(factory);
        using var response = await LoginAsync(client, returnUrl);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNoAuthCookie(response);
    }

    [Fact]
    public async Task Repeated_login_attempts_are_rate_limited()
    {
        await using var factory = new LoginFactory();
        using var client = Client(factory);
        var fields = await FieldsAsync(client);
        fields["password"] = "incorrect";
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var denied = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields));
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using var limited = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields));
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Permission_revocation_denies_tools_but_still_allows_token_protected_logout()
    {
        await using var factory = new LoginFactory();
        using var client = Client(factory);
        using var signedIn = await LoginAsync(client);
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        factory.Roles.AllowAdmin = false;
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/replay")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var forbiddenHub = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        forbiddenHub.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var account = await client.GetStringAsync("/login");
        account.Should().Contain("Sign out");
        using var signedOut = await client.PostAsync("/account/logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(account) }));
        signedOut.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signedOut.Headers.Location!.OriginalString.Should().Be("/login");
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.Found);
        using var anonymousHub = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        anonymousHub.StatusCode.Should().BeOneOf(HttpStatusCode.Found, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("OperatorAuthentication:PasswordHash", "")]
    [InlineData("OperatorAuthentication:PasswordHash", "malformed")]
    [InlineData("BootstrapAdministrator:AdministratorUserId", "")]
    [InlineData("BootstrapAdministrator:AdministratorUserId", "00000000-0000-0000-0000-000000000000")]
    public void Missing_or_invalid_credential_configuration_fails_startup(string key, string value)
    {
        using var factory = new LoginFactory(key, value);
        Action boot = () => _ = factory.Services;
        boot.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    private static HttpClient Client(LoginFactory factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string returnUrl = "/")
    {
        var fields = await FieldsAsync(client);
        fields["returnUrl"] = returnUrl;
        // Actor selection is configuration, never a login form input.
        fields["actorId"] = "22222222-2222-2222-2222-222222222222";
        return await client.PostAsync("/account/login", new FormUrlEncodedContent(fields));
    }

    private static async Task<Dictionary<string, string>> FieldsAsync(HttpClient client) => new()
    {
        ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/login")),
        ["password"] = Password,
    };

    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("the static account form must contain an antiforgery token");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static void AssertNoAuthCookie(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            cookies.Should().NotContain(value => value.StartsWith(".EventSourcingCqrs.AdminConsole.Auth="));
    }

    private sealed class LoginFactory(string? overrideKey = null, string? overrideValue = null)
        : WebApplicationFactory<AdminConsoleHost::Program>
    {
        public MutableRoles Roles { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            // These factories run build output, not a publish directory. Load the build asset manifest.
            builder.UseStaticWebAssets();
            builder.UseSetting("READ_MODEL_CONNECTION_STRING", "Host=localhost;Database=unused;Username=u;Password=p");
            builder.UseSetting("EVENT_STORE_CONNECTION_STRING", "Host=localhost;Database=unused;Username=u;Password=p");
            builder.UseSetting("BootstrapAdministrator:AdministratorUserId", Actor.ToString());
            builder.UseSetting("OperatorAuthentication:PasswordHash",
                new PasswordHasher<string>().HashPassword("operator", Password));
            if (overrideKey is not null) builder.UseSetting(overrideKey, overrideValue);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICurrentUserRolesStore>();
                services.AddSingleton<ICurrentUserRolesStore>(Roles);
            });
        }
    }

    private sealed class MutableRoles : ICurrentUserRolesStore
    {
        public bool AllowAdmin { get; set; } = true;
        public Task<IReadOnlyCollection<Role>> GetRolesForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<Role>>(AllowAdmin && userId == Actor ? [Role.Admin] : []);
        public Task<ICurrentUserRolesUnitOfWork> BeginAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task TruncateAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
