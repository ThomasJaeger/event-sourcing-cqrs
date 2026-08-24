using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventSourcingCqrs.Demo.Seeder;

// The seeder's composition root, named after DemoStartup in the migration demo so the two
// runnable tools read the same way.
//
// It composes the write side and the read side of one process: the command bus the scenarios
// dispatch through, and the projection catch-up waiter they use to know a scenario finished.
// The waiter derives the projections it waits on by resolving closed event handlers from this
// provider, so the read model registration is what makes that derivation return anything.
//
// The event type providers are the write side's other half and are registered here for the same
// reason the Workers host registers them: the registry resolves an event's type name on every
// append and on every stream read, and a name it does not carry fails the append rather than
// degrading. All four contexts register even though the clean scenario writes only two of them,
// because a registry is consulted at the moment a scenario needs it and a missing provider is a
// failure at that moment rather than at composition. The upcaster rides along for the same
// reason: a database that already carries a version-one OrderDrafted is one this tool has to be
// able to read.
//
// The feed head reader is registered directly over the connection factory the event store
// registration already supplies, the same shape the head reader would have taken.
// AddEventStoreHeadPosition is the alternative and its own comment rules it out here: it owns a
// bare data source registration that the full event store registration also owns, so the two are
// not composed in one process.
//
// The port is the feed head rather than the log head, because the waiter needs a target a
// projection can reach. Process manager events raise the log's head and never reach a projection,
// so a scenario that ends with one would wait out its bound against a position no checkpoint can
// equal. Nothing else in this composition reads a head, so the log head is not registered at all.
//
// No process manager event type provider and no command type provider register here. The seeder
// neither reads nor writes a process manager stream and schedules no timeout; both of those live
// in the Workers host, which is where the process managers run.
//
// Nothing here starts a hosted service. Projections advance in the Workers host, so a scenario
// completes only while that host is running, and the waiter's bound is what reports it when it
// is not.
public static class SeederStartup
{
    public static ServiceProvider Compose(
        string eventStoreConnectionString,
        string readModelConnectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventStoreConnectionString);
        ArgumentException.ThrowIfNullOrEmpty(readModelConnectionString);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
        services.AddSingleton<IEventTypeProvider, FulfillmentEventTypeProvider>();
        services.AddSingleton<IEventTypeProvider, BillingEventTypeProvider>();
        services.AddSingleton<IEventTypeProvider, AccessEventTypeProvider>();
        services.AddSingleton<IEventUpcaster, OrderDraftedV1ToV2>();
        services.AddPostgresEventStore(options => options.ConnectionString = eventStoreConnectionString);
        services.AddApplication();
        services.AddReadModels(options => options.ConnectionString = readModelConnectionString);

        services.AddSingleton<IProjectionFeedHeadPosition>(sp =>
            new PostgresProjectionFeedHeadReader(sp.GetRequiredService<INpgsqlConnectionFactory>()));

        services.AddSingleton(sp => new ProjectionCatchUpWaiter(
            sp.GetRequiredService<IProjectionFeedHeadPosition>(),
            sp.GetRequiredService<ICheckpointStore>(),
            sp,
            TimeProvider.System));

        return services.BuildServiceProvider();
    }

    // The resolved services a scenario runs against, parallel to the migration demo's DemoContext.
    // Resolving them once here keeps every scenario's dependencies visible in one place rather than
    // scattered across service-locator calls inside the scenario bodies.
    //
    // The serializer options come from the container rather than from a fresh factory call. The
    // event store registration puts the shared options there and lets a host pre-register its own,
    // and a payload written under those options reads back only under the same ones.
    public static SeederContext CreateContext(ServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new SeederContext(
            provider.GetRequiredService<ICommandBus>(),
            provider.GetRequiredService<IQueryBus>(),
            provider.GetRequiredService<ProjectionCatchUpWaiter>(),
            provider.GetRequiredService<JsonSerializerOptions>());
    }
}

public sealed record SeederContext(
    ICommandBus Commands,
    IQueryBus Queries,
    ProjectionCatchUpWaiter Waiter,
    JsonSerializerOptions JsonOptions);
