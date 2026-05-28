using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Application.SignalR;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components;
using EventSourcingCqrs.Hosts.Web.Hubs;
using EventSourcingCqrs.Infrastructure.SignalR;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// The Api host's base URL. Required: no default. A WebApplicationFactory test
// overrides this via UseSetting against the Api WebApplicationFactory's in-memory
// test server's base address.
var apiBaseUrl = builder.Configuration["API_BASE_URL"]
    ?? throw new InvalidOperationException("API_BASE_URL is not set.");

// Blazor Server only (PLAN.md and setup-doc D8). The optimistic-UI patterns at
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

// SignalR live dashboards (Phase 8, ADR 0027). The hub broadcasts a small
// notification to a per-resource group when a projection commits; subscribing
// pages re-query authoritative state on receipt (notification-only push, D1).
// The backplane LISTENs on the read-model database's projection_committed
// channel, the web host's first Postgres dependency. NotificationContract gives
// the publisher and the backplane one serializer and one channel, so no
// JsonSerializerOptions registration is needed here (Commit 5.5).
var readModelConnectionString = builder.Configuration["READ_MODEL_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("READ_MODEL_CONNECTION_STRING is not set.");
builder.Services.AddSignalR();
builder.Services.AddSingleton<IOptions<HubBackplaneOptions>>(
    Options.Create(new HubBackplaneOptions { ConnectionString = readModelConnectionString }));
builder.Services.AddSingleton<IHubBackplaneConnection, PostgresHubBackplaneConnection>();
builder.Services.AddHostedService<HubBackplaneHostedService>();

var app = builder.Build();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<DashboardHub>("/hubs/dashboard");

app.Run();
