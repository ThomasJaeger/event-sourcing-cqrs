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
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Hosts.Api;
using EventSourcingCqrs.Hosts.Api.Authentication;
using EventSourcingCqrs.Hosts.Api.Endpoints;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using EventSourcingCqrs.Infrastructure.EventStore.Kurrent;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Connection strings come from configuration: the same keys the Workers host reads as environment
// variables, here through IConfiguration so a WebApplicationFactory test can override them with a
// Testcontainer connection (Commit 12). The Api host does not run migrations; the Workers host owns
// migration orchestration, so two hosts never race to migrate the same database.
//
// Which engine holds the events is a configuration choice, not a code change (PLAN.md:253). The
// provider is read once, here, and it is read first because it decides which key addresses the
// engine. The read-model database stays PostgreSQL under its own key regardless of the provider.
var eventStoreProvider = EventStoreProviderSelection.Read(
    builder.Configuration["EVENT_STORE_PROVIDER"]);

// DynamoDB is addressed by a service URL and a table, not a DSN, so this host does not ask for one
// on that provider. The absence is a guard, not a courtesy. Demand a key the engine never reads and
// an operator fills it with the DSN they are migrating off; lose EVENT_STORE_PROVIDER to a
// templating slip after that, and Read's Postgres default composes the old engine against a live
// database and writes events there in silence. Unset, the same slip dies on the throw below.
var dynamoDbServiceUrl = builder.Configuration["EVENT_STORE_DYNAMODB_SERVICE_URL"];
var dynamoDbTableName = builder.Configuration["EVENT_STORE_DYNAMODB_TABLE_NAME"];
var eventStoreConnectionString = eventStoreProvider is EventStoreProvider.DynamoDb
    ? string.Empty
    : builder.Configuration["EVENT_STORE_CONNECTION_STRING"]
        ?? throw new InvalidOperationException("EVENT_STORE_CONNECTION_STRING is not set.");
var readModelConnectionString = builder.Configuration["READ_MODEL_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("READ_MODEL_CONNECTION_STRING is not set.");
EventStoreProviderSelection.ValidateConnectionString(
    eventStoreProvider, eventStoreConnectionString, dynamoDbServiceUrl, dynamoDbTableName);

// Aggregate event types so the event store serializes them on append and resolves
// them when the command handlers this host dispatches rehydrate aggregates. The
// Api host registers no process-manager-event or delay-queue-command providers: it
// neither reads PM streams nor schedules timeouts, both Workers-host concerns.
builder.Services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
// The OrderDrafted version-1 lineage's upcaster, composed into the pipeline beside its event types
// (Chapter 11: Upcasting).
builder.Services.AddSingleton<IEventUpcaster, OrderDraftedV1ToV2>();
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
// process-manager updates. The relational adapters bundle the same companion ports,
// the idempotency store and the delay queue, so their arms leave this host's command
// pipeline fully composed.
_ = eventStoreProvider switch
{
    EventStoreProvider.SqlServer => builder.Services.AddSqlServerEventStore(opts =>
        opts.ConnectionString = eventStoreConnectionString),
    EventStoreProvider.Postgres => builder.Services.AddPostgresEventStore(opts =>
        opts.ConnectionString = eventStoreConnectionString),
    EventStoreProvider.Kurrent => builder.Services.AddKurrentEventStore(opts =>
        opts.ConnectionString = eventStoreConnectionString),
    // DynamoDB sets the two settings that address it instead of a connection string it does not
    // have. Credentials stay with the SDK's own chain: a deployed host resolves them from its role,
    // and no key here can be mistaken for a place to put a secret. An unset table name means the
    // adapter's default, which the Workers host that provisions and drains the table lands on from
    // the same key, so the two cannot name different tables.
    EventStoreProvider.DynamoDb => builder.Services.AddDynamoDbEventStore(opts =>
    {
        // ValidateConnectionString proved this above; this arm is unreachable otherwise.
        opts.ServiceUrl = dynamoDbServiceUrl!;
        if (!string.IsNullOrEmpty(dynamoDbTableName))
        {
            opts.TableName = dynamoDbTableName;
        }
    }),
    _ => throw new InvalidOperationException(
        $"Unhandled event store provider: {eventStoreProvider}."),
};

// Neither KurrentDB nor DynamoDB holds anything but events, and neither adapter bundles a relational
// companion port. The command pipeline still needs the idempotency store, so it composes on the
// read-model PostgreSQL database, where the command-idempotency table lives (migrations 0007 and
// 0019). That is ADR 0046's non-relational posture, and DynamoDB inherits it for the reason
// KurrentDB has it. The delay queue stays absent: no Api registration demands it. Its sole consumer
// is the OrderFulfillment process-manager handler, which lives in the ProcessManagers assembly
// AddApplication does not scan, so this host never registers it.
if (eventStoreProvider is EventStoreProvider.Kurrent or EventStoreProvider.DynamoDb)
{
    builder.Services.AddPostgresIdempotencyStore(readModelConnectionString);
    // The snapshotting Order repository this host resolves needs an ISnapshotStore. KurrentDB and
    // DynamoDB hold only events, so it composes on the read-model PostgreSQL database beside the
    // idempotency store, where the snapshots table lives (migration 0023).
    builder.Services.AddPostgresSnapshotStore(readModelConnectionString);
}
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
