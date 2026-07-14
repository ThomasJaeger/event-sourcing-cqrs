using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Authentication;
using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Hosts.Api;
using EventSourcingCqrs.Hosts.Api.Authentication;
using EventSourcingCqrs.Hosts.Api.Endpoints;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Connection strings come from configuration: the same EVENT_STORE_CONNECTION_STRING
// and READ_MODEL_CONNECTION_STRING keys the Workers host reads as environment
// variables, here through IConfiguration so a WebApplicationFactory test can
// override them with a Testcontainer connection (Commit 12). The Api host does not
// run migrations; the Workers host owns migration orchestration, so two hosts
// never race to migrate the same database.
var eventStoreConnectionString = builder.Configuration["EVENT_STORE_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("EVENT_STORE_CONNECTION_STRING is not set.");
var readModelConnectionString = builder.Configuration["READ_MODEL_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("READ_MODEL_CONNECTION_STRING is not set.");

// Which engine holds the events is a configuration choice, not a code change (PLAN.md:253). The
// provider is read once, here, and governs the single event-store registration below. The read-model
// database stays PostgreSQL under its own key on either provider.
var eventStoreProvider = EventStoreProviderSelection.Read(
    builder.Configuration["EVENT_STORE_PROVIDER"]);
EventStoreProviderSelection.ValidateConnectionString(eventStoreProvider, eventStoreConnectionString);

// Aggregate event types so the event store serializes them on append and resolves
// them when the command handlers this host dispatches rehydrate aggregates. The
// Api host registers no process-manager-event or delay-queue-command providers: it
// neither reads PM streams nor schedules timeouts, both Workers-host concerns.
builder.Services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
builder.Services.AddSingleton<IEventTypeProvider, FulfillmentEventTypeProvider>();
builder.Services.AddSingleton<IEventTypeProvider, BillingEventTypeProvider>();

// Query types so the /queries endpoint (Commit 14) resolves an envelope
// discriminator to a CLR query type through QueryTypeRegistry (ADR 0022).
builder.Services.AddSingleton<IQueryTypeProvider, SalesQueryTypeProvider>();
builder.Services.AddSingleton<IQueryTypeProvider, FulfillmentQueryTypeProvider>();

// User-dispatched command types so the /commands endpoint (Commit 12)
// resolves an envelope discriminator to a CLR command type through
// CommandTypeRegistry (ADR 0023). The Api host registers no delay-queue
// command provider; it dispatches user commands, not timeouts.
builder.Services.AddSingleton<ICommandTypeProvider, SalesCommandTypeProvider>();
builder.Services.AddSingleton<ICommandTypeProvider, FulfillmentCommandTypeProvider>();
builder.Services.AddSingleton<ICommandTypeProvider, BillingCommandTypeProvider>();

// The event store without its outbox processor (Commit 8's split): the Api host
// writes events but does not drain the outbox. The Workers host owns the outbox
// processor; an Api-host processor would race it and drop projection and
// process-manager updates. Both adapters bundle the same companion ports, the
// idempotency store and the delay queue, so either arm leaves this host's command
// pipeline fully composed.
_ = eventStoreProvider switch
{
    EventStoreProvider.SqlServer => builder.Services.AddSqlServerEventStore(opts =>
        opts.ConnectionString = eventStoreConnectionString),
    EventStoreProvider.Postgres => builder.Services.AddPostgresEventStore(opts =>
        opts.ConnectionString = eventStoreConnectionString),
    _ => throw new InvalidOperationException(
        $"Unhandled event store provider: {eventStoreProvider}."),
};
builder.Services.AddApplication();
builder.Services.AddReadModels(opts =>
    opts.ConnectionString = readModelConnectionString);

// Forwarded-identity authentication (Phase 9, ADR 0028). The Api host authenticates a request from
// the X-Forwarded-Identity header an upstream sets: the handler verifies the shared-secret signature,
// the reader parses the header value, and the principal factory loads the actor's authoritative roles
// from the current-roles read model. No new package: the scheme is a custom AuthenticationHandler over
// the framework's authentication primitives, and the signature is HMAC-SHA256 from the base class
// library.
//
// P9.3b: the signature is the enforced credential. The shared secret comes from one configuration key
// both hosts read; the verifier is constructed here so its constructor guard runs as the container is
// built, making a missing or under-length secret a startup failure rather than a first-request one.
// An unsigned or wrongly-signed header fails authentication, so the host no longer relies on a
// trusted-upstream posture to keep the header honest.
var signingSecret = builder.Configuration["FORWARDED_IDENTITY_SIGNING_SECRET"]
    ?? throw new InvalidOperationException("FORWARDED_IDENTITY_SIGNING_SECRET is not set.");
var signingKey = new ForwardedIdentitySigningKey(
    new ForwardedIdentitySigningOptions { Secret = signingSecret });
builder.Services.AddSingleton(signingKey);
builder.Services.AddSingleton(new ForwardedIdentitySignatureVerifier(signingKey));
builder.Services.AddSingleton<IForwardedIdentityReader, HeaderForwardedIdentityReader>();
builder.Services.AddSingleton<IPrincipalFactory, CurrentRolesPrincipalFactory>();
builder.Services.AddAuthentication(ForwardedIdentityDefaults.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ForwardedIdentityAuthenticationHandler>(
        ForwardedIdentityDefaults.SchemeName, configureOptions: null);
builder.Services.AddAuthorization();

var app = builder.Build();

// Authentication and authorization run inside the exception-mapping middleware (so a dispatch
// exception still maps) and before the endpoints (so an unauthenticated request to a gated route is
// a 401 challenge that never reaches the handler).
app.UseMiddleware<ExceptionMappingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
// Both POST dispatch routes require authentication (Phase 9). The GET introspection routes stay
// open: they publish only the catalog of accepted type tokens, no data.
app.MapPost("/commands", CommandsEndpoint.HandleAsync).RequireAuthorization();
app.MapPost("/queries", QueriesEndpoint.HandleAsync).RequireAuthorization();
// The Web host's dashboard hub calls this before joining a SignalR group, to authorize the subscribe
// against the same gate and ownership rule a read of the resource runs under (P9.6).
app.MapPost("/subscriptions/authorize", SubscriptionsEndpoint.AuthorizeAsync).RequireAuthorization();
app.MapGet("/commands", IntrospectionEndpoint.ListCommands);
app.MapGet("/queries", IntrospectionEndpoint.ListQueries);

app.Run();

// Exposed so the IntegrationTests project's WebApplicationFactory<Program> can
// boot this composition in-memory against a Testcontainer Postgres.
public partial class Program { }
