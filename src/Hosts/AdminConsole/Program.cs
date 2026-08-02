using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Hosts.AdminConsole;
using EventSourcingCqrs.Hosts.AdminConsole.Authorization;
using EventSourcingCqrs.Hosts.AdminConsole.Browser;
using EventSourcingCqrs.Hosts.AdminConsole.Components;
using EventSourcingCqrs.Hosts.AdminConsole.Replay;
using EventSourcingCqrs.Hosts.AdminConsole.Tracer;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using EventSourcingCqrs.Infrastructure.EventStore.Kurrent;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Infrastructure.SignalR;
using EventSourcingCqrs.Projections.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Chapter 17: the AdminConsole operator host. A Blazor Server host that fails closed (ADR 0040): a
// host-level fallback policy gates every route, so an unauthenticated request is challenged with a
// redirect to the login path. The interactive login surface is a later slice, so that path does not
// resolve yet; the redirect is the declared fail-closed interim.
var builder = WebApplication.CreateBuilder(args);

// Validate the DI graph on build in every environment. The default builder validates only in
// Development; making it unconditional fails the host closed at startup on a missing or
// over-provisioned registration rather than surfacing the defect at the first operator action, the
// same fail-closed posture as the throw-on-missing connection-string guards below.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The deny path's two dependencies: the permission authorizer and the current-roles read the handler
// resolves roles through. The console composes focused read registrations rather than the full
// command, event-store, or projection stacks. Throw-on-missing
// at startup, the same guard the Web host uses, so a misconfigured deployment fails to boot rather
// than failing the first authorization decision.
var readModelConnectionString = builder.Configuration["READ_MODEL_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("READ_MODEL_CONNECTION_STRING is not set.");
builder.Services.AddPermissionAuthorization();
builder.Services.AddCurrentRolesReadModel(options => options.ConnectionString = readModelConnectionString);

// The Projection Status Dashboard reads projection lag in process (ADR 0040): the head of the event
// stream minus each projection's checkpoint. It composes the reader's three ports focused, with no
// read-model or event-store over-provisioning. The checkpoint store comes from AddCurrentRolesReadModel
// above; the arm below adds only the head read and its connection, not the full event store;
// AddProjectionRoster adds the name-only projection set.
//
// Which engine holds the events is a configuration choice (PLAN.md's Phase 2 provider-switch done-when). The console reads it through
// the same twin the Api and Workers hosts use, retiring the earlier PostgreSQL-only inline guard, and
// composes its three read-side event-store ports (head position, replay/browse IEventStore, correlation
// trace) plus the correlation-tracer capability per arm. The read-model side below stays PostgreSQL
// regardless of the provider, and every read-side collaborator below reaches the arm's ports through
// their abstractions. The provider is read first because it decides which key addresses the engine.
var eventStoreProvider = EventStoreProviderSelection.Read(builder.Configuration["EVENT_STORE_PROVIDER"]);

// DynamoDB is addressed by a service URL and a table, not a DSN, so this host does not ask for one on
// that provider, the same flat surface the Api and Workers hosts read. The absence is a guard rather
// than a courtesy: demand a key the engine never reads and an operator fills it with the DSN they are
// migrating off, and a dropped provider key then lands on Read's Postgres default and composes the old
// engine against a live database. Unset, that slip dies on the throw below. Throw-on-missing for the
// engines that do have a DSN, the same guard the read-model connection uses.
var dynamoDbServiceUrl = builder.Configuration["EVENT_STORE_DYNAMODB_SERVICE_URL"];
var dynamoDbTableName = builder.Configuration["EVENT_STORE_DYNAMODB_TABLE_NAME"];
var eventStoreConnectionString = eventStoreProvider is EventStoreProvider.DynamoDb
    ? string.Empty
    : builder.Configuration["EVENT_STORE_CONNECTION_STRING"]
        ?? throw new InvalidOperationException("EVENT_STORE_CONNECTION_STRING is not set.");

switch (eventStoreProvider)
{
    case EventStoreProvider.Postgres:
        EventStoreProviderSelection.ValidateConnectionString(
            eventStoreProvider, eventStoreConnectionString);
        builder.Services.AddEventStoreHeadPosition(eventStoreConnectionString);
        builder.Services.AddEventStoreReplayReader();
        builder.Services.AddCorrelationTraceReader(eventStoreConnectionString);
        builder.Services.AddSingleton(CorrelationTracerAvailability.Available);
        break;
    case EventStoreProvider.Kurrent:
        EventStoreProviderSelection.ValidateConnectionString(
            eventStoreProvider, eventStoreConnectionString);
        builder.Services.AddKurrentEventStore(opts => opts.ConnectionString = eventStoreConnectionString);
        builder.Services.AddKurrentEventStoreHeadPosition();
        // KurrentDB carries no cross-stream correlation-id index, so the Correlation-ID Tracer is
        // unavailable and the /correlations page renders a notice instead of the trace surface. The
        // reader is the defense-in-depth throwing one, registered so the port resolves and the tracer
        // seam composes; the capability keeps the page from ever reaching it in normal operation.
        builder.Services.AddSingleton<ICorrelationTraceReader, KurrentCorrelationTraceReader>();
        builder.Services.AddSingleton(CorrelationTracerAvailability.Unavailable(
            "The KurrentDB event store has no cross-stream correlation-id index; a correlation trace "
            + "would need a dedicated user projection, which is deferred."));
        break;
    case EventStoreProvider.DynamoDb:
        EventStoreProviderSelection.ValidateConnectionString(
            eventStoreProvider, eventStoreConnectionString, dynamoDbServiceUrl, dynamoDbTableName);
        builder.Services.AddDynamoDbEventStore(opts =>
        {
            // ValidateConnectionString proved the service URL above; this arm is unreachable otherwise.
            opts.ServiceUrl = dynamoDbServiceUrl!;
            if (!string.IsNullOrEmpty(dynamoDbTableName))
            {
                opts.TableName = dynamoDbTableName;
            }
        });
        builder.Services.AddDynamoDbEventStoreHeadPosition();
        // DynamoDB carries no cross-stream metadata index either. The table's only key is the stream
        // id and the log partition's only order is the global position, so finding every event with
        // one correlation id would scan the whole log. The Correlation-ID Tracer is unavailable for
        // the reason it is unavailable on KurrentDB, and the shape is the same: the throwing reader
        // registers so the port resolves and the tracer seam composes, and the capability keeps the
        // page from ever reaching it.
        builder.Services.AddSingleton<ICorrelationTraceReader, DynamoDbCorrelationTraceReader>();
        builder.Services.AddSingleton(CorrelationTracerAvailability.Unavailable(
            "The DynamoDB event store has no cross-stream correlation-id index; a correlation trace "
            + "would scan the whole log, so it needs a dedicated read path, which is deferred."));
        break;
    case EventStoreProvider.SqlServer:
        // The AdminConsole has no SQL Server read side: its three ports are hand-rolled PostgreSQL. It
        // refuses to compose rather than boot green on a lazy data source and fail at the first click.
        throw new InvalidOperationException(
            $"EVENT_STORE_PROVIDER is '{eventStoreProvider}'. The AdminConsole composes PostgreSQL-only "
            + "read-side event-store ports and cannot serve another engine.");
    default:
        throw new InvalidOperationException(
            $"Unhandled event store provider: {eventStoreProvider}.");
}

// The Projection Status Dashboard reads projection lag in process (ADR 0040): the head of the event
// stream minus each projection's checkpoint. AddProjectionRoster adds the name-only projection set; the
// checkpoint store comes from AddCurrentRolesReadModel above, and the head reader from the arm above.
builder.Services.AddProjectionRoster();
builder.Services.AddSingleton<ProjectionLagReader>();

// The Replay Tool's per-tenant rebuild (Phase 12, ADR 0041) and the Event Store Browser (Phase 12) both
// read the event stream through the IEventStore the arm above composed. The four event-type providers
// register host-side (the adapter idiom); the full four-context set is required because a tenant's
// history can span every context and ReadAllForTenantAsync throws on any unregistered type. The tenant
// accessor and the notification publisher are the throughput store's remaining dependencies.
builder.Services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
// The OrderDrafted version-1 lineage's upcaster, composed into the pipeline beside its event types
// (Chapter 11: Upcasting).
builder.Services.AddSingleton<IEventUpcaster, OrderDraftedV1ToV2>();
builder.Services.AddSingleton<IEventTypeProvider, FulfillmentEventTypeProvider>();
builder.Services.AddSingleton<IEventTypeProvider, BillingEventTypeProvider>();
builder.Services.AddSingleton<IEventTypeProvider, AccessEventTypeProvider>();
builder.Services.AddSingleton<ICurrentTenantAccessor, AsyncLocalCurrentTenantAccessor>();
builder.Services.TryAddSingleton<PostgresPgNotifyPublisher>();
builder.Services.AddSingleton<IOrderThroughputStore, PostgresOrderThroughputStore>();
builder.Services.AddSingleton<PerTenantProjectionRebuilder>();
builder.Services.AddSingleton<IOrderThroughputRebuild, OrderThroughputRebuild>();
builder.Services.AddSingleton<IStreamInspector, StreamInspector>();

// The Correlation-ID Tracer's seam (Phase 12, ADR 0043). It holds the row cap and hands it to the
// ICorrelationTraceReader the arm above composed. On KurrentDB that reader is the throwing one and the
// capability gates the page, so the seam composes but is never driven.
builder.Services.AddSingleton<ICorrelationTracer, CorrelationTracer>();

// Cookie authentication for the operator. The cookie is HttpOnly and Secure-always, so the host
// requires an https endpoint. An unauthenticated request is challenged with a redirect to LoginPath.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".EventSourcingCqrs.AdminConsole.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
    });

// Deny-by-default (ADR 0040): the fallback policy gates every endpoint that carries no other
// authorization metadata, so a page added without an attribute is denied rather than exposed. The
// AdminConsoleAccessHandler satisfies the requirement only for an operator whose roles grant console
// access.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .AddRequirements(new AdminConsoleAccessRequirement())
        .Build());
builder.Services.AddSingleton<IAuthorizationHandler, AdminConsoleAccessHandler>();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Authentication and authorization run before the component endpoints, so the fallback policy
// challenges an unauthenticated request before it reaches a page. HttpsRedirection is first: the
// Secure-always cookie is only sent over https.
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Exposed so the IntegrationTests project's WebApplicationFactory can boot this composition in-memory.
public partial class Program { }
