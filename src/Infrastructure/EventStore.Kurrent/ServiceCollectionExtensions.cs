using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.Versioning;
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

        services.TryAddSingleton(_ => EventStoreJsonOptions.Create());

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

        // The upcaster pipeline reads the same registry the store reads, plus the IEventUpcaster
        // links a host contributes. Empty by default: with no upcasters registered, every event is
        // already at its current version, so the read path is unchanged.
        services.TryAddSingleton(sp => new EventUpcasterPipeline(
            sp.GetRequiredService<EventTypeRegistry>(),
            sp.GetServices<IEventUpcaster>()));

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

    // Registers the KurrentDB subscription dispatch service, the native read-side mechanism the Workers
    // host composes in place of an outbox processor. KurrentDB pushes committed events to a catch-up
    // subscription, so this drain has no processor to poll and no outbox table to read.
    public static IServiceCollection AddKurrentSubscriptionService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<KurrentSubscriptionOptions>();

        // The in-process dispatcher the subscription plays events into, resolving projections and
        // process-manager handlers per event through the same class the relational outbox processors
        // use (ADR 0004 covers the engine-specific mechanics, not this consumer-side resolver). TryAdd
        // so a host that also composed a relational path keeps its single dispatcher.
        services.TryAddSingleton<IMessageDispatcher, InProcessMessageDispatcher>();
        services.AddHostedService<KurrentSubscriptionService>();

        return services;
    }

    // Registers the KurrentDB head-position reader for the AdminConsole's projection-lag read, the
    // engine-specific counterpart to AddEventStoreHeadPosition on the relational side. It reads $all
    // backwards from its end, so it needs only the client the AddKurrentEventStore registration provides;
    // a host composes both. TryAdd so it composes beside that registration without racing it.
    public static IServiceCollection AddKurrentEventStoreHeadPosition(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IEventStoreHeadPosition, KurrentEventStoreHeadReader>();

        return services;
    }
}
