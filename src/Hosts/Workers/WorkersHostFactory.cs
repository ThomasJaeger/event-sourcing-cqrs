using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.ProcessManagers;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment;
using EventSourcingCqrs.ProcessManagers.Returns;
using EventSourcingCqrs.Projections.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Hosts.Workers;

// Composes the Workers host wiring in one place. Static so the integration
// test in tests/Workers.Tests can build the same host shape Program.cs
// builds against testcontainer connection strings, without duplicating
// registrations. Migrations orchestration lives in Program.cs, not here:
// the test fixture migrates ahead of the test, so the test path stays
// migration-free.
public static class WorkersHostFactory
{
    public static IHost Build(
        EventStoreProvider eventStoreProvider,
        string eventStoreConnectionString,
        string readModelConnectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventStoreConnectionString);
        ArgumentException.ThrowIfNullOrEmpty(readModelConnectionString);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
        builder.Services.AddSingleton<IEventTypeProvider, FulfillmentEventTypeProvider>();
        builder.Services.AddSingleton<IEventTypeProvider, BillingEventTypeProvider>();
        builder.Services.AddSingleton<IEventTypeProvider, AccessEventTypeProvider>();
        // Process-manager event types resolve through the separate PM registry
        // (ADR 0013); the event-store registration walks every IProcessManagerEventTypeProvider.
        builder.Services.AddSingleton<IProcessManagerEventTypeProvider, OrderFulfillmentEventTypeProvider>();
        builder.Services.AddSingleton<IProcessManagerEventTypeProvider, ReturnEventTypeProvider>();
        // The OrderFulfillment timeout commands round-trip through the delay queue,
        // so their types register in CommandTypeRegistry (ADR 0017).
        builder.Services.AddSingleton<ICommandTypeProvider, OrderFulfillmentCommandTypeProvider>();
        // The provider the caller selected picks the engine for all three write-side registrations
        // below. They are the only engine-specific ones: everything else in this composition is a
        // port, and the read models stay PostgreSQL under their own connection string.
        if (eventStoreProvider == EventStoreProvider.SqlServer)
        {
            builder.Services.AddSqlServerEventStore(opts =>
                opts.ConnectionString = eventStoreConnectionString);
        }
        else
        {
            builder.Services.AddPostgresEventStore(opts =>
                opts.ConnectionString = eventStoreConnectionString);
        }
        // The outbox processor drains events to the in-process dispatcher and needs
        // no command bus, so it composes right after the event store (the
        // delay-queue processor below needs AddApplication first; the outbox does
        // not). Neither event-store registration bundles it.
        if (eventStoreProvider == EventStoreProvider.SqlServer)
        {
            builder.Services.AddSqlServerOutboxProcessor();
        }
        else
        {
            builder.Services.AddPostgresOutboxProcessor();
        }
        builder.Services.AddApplication();
        // After AddApplication so the delay-queue processor's ICausedCommandBus
        // dependency is resolvable (ADR 0017).
        if (eventStoreProvider == EventStoreProvider.SqlServer)
        {
            builder.Services.AddSqlServerDelayQueueProcessor();
        }
        else
        {
            builder.Services.AddPostgresDelayQueueProcessor();
        }
        builder.Services.AddReadModels(opts =>
            opts.ConnectionString = readModelConnectionString);

        // Process-manager handlers and their collaborators. The outbox dispatcher
        // resolves them by IProcessManagerHandler<T> (commit 27); they are scoped
        // because they depend on the scoped aggregate and PM repositories. The
        // timeout command handlers live outside the Application assembly
        // AddApplication scans, so they register explicitly.
        builder.Services.AddScoped<OrderFulfillmentCompensation>();
        builder.Services.AddProcessManagerHandler<OrderFulfillmentProcessManagerHandler>();
        builder.Services.AddProcessManagerHandler<ReturnProcessManagerHandler>();
        builder.Services.AddScoped<ICommandHandler<TimeoutAwaitingPaymentForOrder>,
            TimeoutAwaitingPaymentForOrderHandler>();
        builder.Services.AddScoped<ICommandHandler<TimeoutAwaitingDispatchForOrder>,
            TimeoutAwaitingDispatchForOrderHandler>();

        // Bootstrap administrator seed (Phase 9, ADR 0028): grants the configured administrator the
        // Admin role on startup so the system has an assigner before any assignment can be made.
        // Registered before the projection catch-up so its StartingAsync runs first and the catch-up
        // replays the seeded event into the current-roles read model. An unset id disables the seed.
        var bootstrapAdministratorId =
            Guid.TryParse(
                builder.Configuration["BootstrapAdministrator:AdministratorUserId"], out var adminId)
                ? adminId
                : Guid.Empty;
        builder.Services.AddSingleton<IOptions<BootstrapAdministratorOptions>>(
            Options.Create(new BootstrapAdministratorOptions { AdministratorUserId = bootstrapAdministratorId }));
        builder.Services.AddHostedService<BootstrapAdministratorService>();

        builder.Services.AddHostedService<ProjectionStartupCatchUpService>();
        return builder.Build();
    }
}
