using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
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
// The head reader is registered directly over the connection factory the event store
// registration already supplies. AddEventStoreHeadPosition is the alternative, and its own
// comment rules it out here: it owns a bare data source registration that the full event store
// registration also owns, so the two are not composed in one process.
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
        services.AddPostgresEventStore(options => options.ConnectionString = eventStoreConnectionString);
        services.AddApplication();
        services.AddReadModels(options => options.ConnectionString = readModelConnectionString);

        services.AddSingleton<IEventStoreHeadPosition>(sp =>
            new PostgresEventStoreHeadReader(sp.GetRequiredService<INpgsqlConnectionFactory>()));

        services.AddSingleton(sp => new ProjectionCatchUpWaiter(
            sp.GetRequiredService<IEventStoreHeadPosition>(),
            sp.GetRequiredService<ICheckpointStore>(),
            sp,
            TimeProvider.System));

        return services.BuildServiceProvider();
    }
}
