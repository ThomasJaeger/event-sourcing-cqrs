using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.ProcessManagers;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment;
using EventSourcingCqrs.ProcessManagers.Returns;
using EventSourcingCqrs.Projections.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        string eventStoreConnectionString,
        string readModelConnectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventStoreConnectionString);
        ArgumentException.ThrowIfNullOrEmpty(readModelConnectionString);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
        builder.Services.AddSingleton<IEventTypeProvider, FulfillmentEventTypeProvider>();
        builder.Services.AddSingleton<IEventTypeProvider, BillingEventTypeProvider>();
        // Process-manager event types resolve through the separate PM registry
        // (ADR 0013); AddPostgresEventStore walks every IProcessManagerEventTypeProvider.
        builder.Services.AddSingleton<IProcessManagerEventTypeProvider, OrderFulfillmentEventTypeProvider>();
        builder.Services.AddSingleton<IProcessManagerEventTypeProvider, ReturnEventTypeProvider>();
        // The OrderFulfillment timeout commands round-trip through the delay queue,
        // so their types register in CommandTypeRegistry (ADR 0017).
        builder.Services.AddSingleton<ICommandTypeProvider, OrderFulfillmentCommandTypeProvider>();
        builder.Services.AddPostgresEventStore(opts =>
            opts.ConnectionString = eventStoreConnectionString);
        // The outbox processor drains events to the in-process dispatcher and needs
        // no command bus, so it composes right after the event store (the
        // delay-queue processor below needs AddApplication first; the outbox does
        // not). AddPostgresEventStore no longer bundles it.
        builder.Services.AddPostgresOutboxProcessor();
        builder.Services.AddApplication();
        // After AddApplication so the delay-queue processor's ICausedCommandBus
        // dependency is resolvable (ADR 0017).
        builder.Services.AddPostgresDelayQueueProcessor();
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

        builder.Services.AddHostedService<ProjectionStartupCatchUpService>();
        return builder.Build();
    }
}
