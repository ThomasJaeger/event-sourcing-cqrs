using EventSourcingCqrs.Domain.Abstractions;
using KurrentDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

public static class ServiceCollectionExtensions
{
    // Composes the KurrentDB event store: the client settings and client, the JSON options, the
    // three type registries populated by the same provider walk the relational adapters use, and
    // the store. No outbox and no companion ports yet; the companion-port ruling (idempotency and
    // delay queue on a non-relational engine) and the host provider arm land in later slices.
    //
    // TryAdd throughout, a deliberate divergence from AddPostgresEventStore's plain Adds. The
    // relational adapters own the sole bare NpgsqlDataSource in a host and register the store with
    // a plain Add; the KurrentDB arm composes into a host that also composes the read-model side
    // against the same PostgreSQL database, so registering defensively keeps a shared dependency
    // from being double-registered. The slice-6 ADR records this as the adapter's registration
    // posture.
    public static IServiceCollection AddKurrentEventStore(
        this IServiceCollection services,
        Action<KurrentEventStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.TryAddSingleton(_ => KurrentEventStoreJsonOptions.Create());

        services.TryAddSingleton<KurrentDBClientSettings>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<KurrentEventStoreOptions>>().Value;
            return KurrentDBClientSettings.Create(opts.ConnectionString);
        });

        services.TryAddSingleton<KurrentDBClient>(sp =>
            new KurrentDBClient(sp.GetRequiredService<KurrentDBClientSettings>()));

        // Providers contribute by bounded context, exactly as on the relational side. The factory
        // walks every registered provider once on first resolution; the registry is immutable
        // from there.
        services.TryAddSingleton<EventTypeRegistry>(sp =>
        {
            var registry = new EventTypeRegistry();
            foreach (var provider in sp.GetServices<IEventTypeProvider>())
            {
                foreach (var eventType in provider.GetEventTypes())
                {
                    registry.Register(eventType);
                }
            }
            return registry;
        });

        // PM event types resolve through a separate registry (ADR 0013).
        services.TryAddSingleton<ProcessManagerEventTypeRegistry>(sp =>
        {
            var registry = new ProcessManagerEventTypeRegistry();
            foreach (var provider in sp.GetServices<IProcessManagerEventTypeProvider>())
            {
                foreach (var eventType in provider.GetEventTypes())
                {
                    registry.Register(eventType);
                }
            }
            return registry;
        });

        // Command types resolve through CommandTypeRegistry for the delay queue (ADR 0017),
        // registered here for parity with the relational composition even though slice 1 does not
        // consume it.
        services.TryAddSingleton<CommandTypeRegistry>(sp =>
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

        services.TryAddSingleton<IEventStore, KurrentEventStore>();

        return services;
    }
}
