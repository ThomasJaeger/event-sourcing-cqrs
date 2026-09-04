using System.Security.Claims;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Application.SignalR;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Authentication;
using EventSourcingCqrs.Hosts.Web.Components;
using EventSourcingCqrs.Hosts.Web.Hubs;
using EventSourcingCqrs.Infrastructure.SignalR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// The Api host's base URL. Required: no default. A WebApplicationFactory test
// overrides this via UseSetting against the Api WebApplicationFactory's in-memory
// test server's base address.
var apiBaseUrl = builder.Configuration["API_BASE_URL"]
    ?? throw new InvalidOperationException("API_BASE_URL is not set.");

// The shared forwarded-identity signing secret (P9.3b). The same configuration key the Api host
// reads, so the Web signer and the Api verifier agree by construction (2a). Throw-on-missing at
// startup, the same idiom the connection strings use.
var signingSecret = builder.Configuration["FORWARDED_IDENTITY_SIGNING_SECRET"]
    ?? throw new InvalidOperationException("FORWARDED_IDENTITY_SIGNING_SECRET is not set.");

// The actor the cookie login establishes. The same configuration key the Workers host's bootstrap
// administrator seed reads, so the logged-in operator is the actor that seed granted Admin, and the
// Api host loads that actor's authoritative roles. Throw-on-missing here, unlike the Workers'
// tolerant empty-default: a login with no configured subject has no meaning.
var loginActorId =
    Guid.TryParse(builder.Configuration["BootstrapAdministrator:AdministratorUserId"], out var configuredActorId)
        && configuredActorId != Guid.Empty
        ? configuredActorId
        : throw new InvalidOperationException("BootstrapAdministrator:AdministratorUserId is not set.");

// Blazor Server only. The optimistic-UI patterns at
// Cluster 4 Commit 23 are pure server-circuit concerns; no WASM render mode buys
// anything for the v1 page set, and the Server-only composition is the simplest
// container for the pending-badge state machine and polling loop.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// TimeProvider for the optimistic-UI polling loop's delays and deadline. Tests
// register a FakeTimeProvider to drive the loop deterministically.
builder.Services.AddSingleton(TimeProvider.System);

// Type-provider registration mirrors the Api host's set: the Web host's IApiClient
// builds envelope discriminators for every command and query it dispatches, so the
// providers it registers must cover the same surface the Api host accepts. No
// IEventTypeProvider registrations: the Web host neither reads nor writes event
// streams.
builder.Services.AddSingleton<ICommandTypeProvider, SalesCommandTypeProvider>();
builder.Services.AddSingleton<ICommandTypeProvider, FulfillmentCommandTypeProvider>();
builder.Services.AddSingleton<ICommandTypeProvider, BillingCommandTypeProvider>();
builder.Services.AddSingleton<IQueryTypeProvider, SalesQueryTypeProvider>();
builder.Services.AddSingleton<IQueryTypeProvider, FulfillmentQueryTypeProvider>();

// The two registries inlined here rather than reached through AddPostgresEventStore
// or AddApplication. The Web host dispatches over HTTP through IApiClient, never
// resolving an ICommandBus or IQueryBus, so the full event-store and application
// composition would register infrastructure this host neither consumes nor can
// configure (it has no event-store connection string and no read-model connection
// string). Six lines of registry composition stay local until a second host with
// the same shape earns an extraction.
builder.Services.AddSingleton<CommandTypeRegistry>(sp =>
{
    var registry = new CommandTypeRegistry();
    foreach (var provider in sp.GetServices<ICommandTypeProvider>())
    {
        foreach (var commandType in provider.GetCommandTypes())
        {
            registry.Register(commandType);
        }
    }
    return registry;
});
builder.Services.AddSingleton<QueryTypeRegistry>(sp =>
{
    var registry = new QueryTypeRegistry();
    foreach (var provider in sp.GetServices<IQueryTypeProvider>())
    {
        foreach (var queryType in provider.GetQueryTypes())
        {
            registry.Register(queryType);
        }
    }
    return registry;
});

// IApiClient is the Web host's only outbound dependency on the Api host (D-W).
// Typed HttpClient registration: AddHttpClient<TClient, TImplementation> ties the
// client's HttpClient lifetime to a per-resolution scope managed by
// IHttpClientFactory, with the base address fixed to the Api host. Symmetric
// serialization with the Api host's web-default options happens inside ApiClient.
builder.Services.AddHttpClient<IApiClient, ApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

// The dashboard hub authorizes a subscribe by asking the Api host whether the caller may read the
// resource (P9.6). Its own typed client to the same Api host signs with the actor on Context.User
// rather than the circuit-scoped provider ApiClient uses, because a hub method runs on a SignalR
// connection with no Blazor circuit. ForwardedIdentitySigner is the singleton registered just below
// for ApiClient; DI resolves it when the client is constructed.
builder.Services.AddHttpClient<ISubscriptionAuthorizationClient, SubscriptionAuthorizationClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    // 10 seconds, not the 100-second HttpClient default: the authorize call is an in-cluster round
    // trip to the Api host, and a subscribe should not hold a page's arm open longer (ADR 0035).
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Forwarded-identity signing (P9.3b). The Web host signs every dispatched request with the circuit's
// actor under the shared key, so the Api host (signature-mandatory since Commit 1) accepts it. The
// signing key's constructor guards the secret, so a missing or under-length secret fails at startup.
// The circuit identity provider is scoped per Blazor Server circuit (the host's first scoped service);
// ApiClient reads it at send time, which is why signing lives in the client and not a pooled handler.
builder.Services.AddSingleton(new ForwardedIdentitySigningKey(
    new ForwardedIdentitySigningOptions { Secret = signingSecret }));
builder.Services.AddSingleton<ForwardedIdentitySigner>();
builder.Services.AddScoped<ICircuitForwardedIdentityProvider, CircuitForwardedIdentityProvider>();

// Cookie authentication for the operator login (P9.3b). The configured-actor login is a development
// and same-trust-boundary credential, not proof of identity; real proof is deferred to an external
// identity provider (out of scope, ADR 0028). The cookie is HttpOnly and Secure-always, so the host
// requires an https endpoint (it expects ASPNETCORE_URLS to carry https; no launch profile ships).
// The framework seeds the InteractiveServer circuit's authentication state from this cookie's
// principal through the default ServerAuthenticationStateProvider on .NET 10, so no explicit
// AuthenticationStateProvider registration and no auth-state serialization are needed for a
// Server-only host. No revalidating provider: revalidation is the external identity provider's
// concern, not faked here.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".EventSourcingCqrs.Web.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);

// In-process live dashboards (ADR 0032, superseding the SignalR hub of ADR 0027). The backplane LISTENs
// on the read-model database's projection_committed channel (the web host's first Postgres dependency);
// the hosted reader feeds the in-process dispatcher, which fans each notification out to the circuit-scoped
// subscribers. NotificationContract gives the publisher and the backplane one serializer and one channel.
var readModelConnectionString = builder.Configuration["READ_MODEL_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("READ_MODEL_CONNECTION_STRING is not set.");
builder.Services.AddSingleton<IOptions<HubBackplaneOptions>>(
    Options.Create(new HubBackplaneOptions { ConnectionString = readModelConnectionString }));
builder.Services.AddSingleton<IHubBackplaneConnection, PostgresHubBackplaneConnection>();
// The in-process notification fan-out (ADR 0032, superseding the SignalR hub broadcast). One dispatcher
// for the whole host (singleton) so a projection change reaches every circuit/tab; one subscription per
// page (transient), since the page owns and disposes it in its own DisposeAsync, so a second same-circuit
// navigation gets a fresh one rather than the instance the prior page already disposed.
builder.Services.AddSingleton<IResourceNotificationDispatcher, ResourceNotificationDispatcher>();
builder.Services.AddTransient<ICircuitResourceSubscription, CircuitResourceSubscription>();
builder.Services.AddHostedService<HubBackplaneHostedService>();

var app = builder.Build();

// Authentication and authorization run before the component endpoints and the hub, so the circuit and
// the login/logout endpoints see the cookie principal. Antiforgery guards the login and logout form
// posts. HttpsRedirection is first: the Secure-always cookie is only sent over https.
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// The operator login and logout. SignInAsync establishes a name-identifier-only principal for the
// configured actor; the framework seeds the circuit from it. Antiforgery is validated in the handler
// rather than relying on UseAntiforgery: the middleware validates Razor Component form handlers and
// form-binding minimal-API endpoints, not a plain form post read through HttpContext.Request.Form, so
// the explicit ValidateRequestAsync is the single validation site for these two endpoints.
app.MapPost("/account/login", async (HttpContext httpContext, IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(httpContext);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("The antiforgery token was missing or invalid.");
    }

    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, loginActorId.ToString()) };
    var principal = new ClaimsPrincipal(
        new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

    var returnUrl = httpContext.Request.Form["returnUrl"].ToString();
    return Results.LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
});

app.MapPost("/account/logout", async (HttpContext httpContext, IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(httpContext);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("The antiforgery token was missing or invalid.");
    }

    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

app.Run();

// Exposed so the IntegrationTests project's WebApplicationFactory can boot this composition in-memory.
public partial class Program { }
