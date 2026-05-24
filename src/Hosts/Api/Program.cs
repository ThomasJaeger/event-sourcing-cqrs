using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

var builder = WebApplication.CreateBuilder(args);

// Connection strings come from configuration: the same EVENT_STORE_CONNECTION_STRING
// and READ_MODEL_CONNECTION_STRING keys the Workers host reads as environment
// variables, here through IConfiguration so a WebApplicationFactory test can
// override them with a Testcontainer connection (Commit 10). The Api host does not
// run migrations; the Workers host owns migration orchestration, so two hosts
// never race to migrate the same database.
var eventStoreConnectionString = builder.Configuration["EVENT_STORE_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("EVENT_STORE_CONNECTION_STRING is not set.");
var readModelConnectionString = builder.Configuration["READ_MODEL_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("READ_MODEL_CONNECTION_STRING is not set.");

// Aggregate event types so the event store serializes them on append and resolves
// them when the command handlers this host dispatches rehydrate aggregates. The
// Api host registers no process-manager-event or delay-queue-command providers: it
// neither reads PM streams nor schedules timeouts, both Workers-host concerns.
builder.Services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
builder.Services.AddSingleton<IEventTypeProvider, FulfillmentEventTypeProvider>();
builder.Services.AddSingleton<IEventTypeProvider, BillingEventTypeProvider>();

// Query types so the /queries endpoint (Commit 11) resolves an envelope
// discriminator to a CLR query type through QueryTypeRegistry (ADR 0022).
builder.Services.AddSingleton<IQueryTypeProvider, SalesQueryTypeProvider>();
builder.Services.AddSingleton<IQueryTypeProvider, FulfillmentQueryTypeProvider>();

// AddPostgresEventStore without AddPostgresOutboxProcessor (Commit 8's split): the
// Api host writes events but does not drain the outbox. The Workers host owns the
// outbox processor; an Api-host processor would race it and drop projection and
// process-manager updates.
builder.Services.AddPostgresEventStore(opts =>
    opts.ConnectionString = eventStoreConnectionString);
builder.Services.AddApplication();
builder.Services.AddReadModels(opts =>
    opts.ConnectionString = readModelConnectionString);

var app = builder.Build();

// Endpoints and the exception-mapping middleware land at Commits 10-12
// (POST /commands, POST /queries, the GET introspection endpoints).

app.Run();
