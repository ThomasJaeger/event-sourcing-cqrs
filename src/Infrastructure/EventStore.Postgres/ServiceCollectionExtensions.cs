using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresEventStore(
        this IServiceCollection services,
        Action<PostgresEventStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        // Defaults a host can pre-empt. A host that needs custom data-source
        // wiring (logging integration, custom type mappings, connection
        // multiplexing) registers its own NpgsqlDataSource before calling
        // this extension. JsonSerializerOptions defaults to snake_case_lower
        // so the payload and metadata round-trip through the schema's STORED
        // generated columns; it freezes on first use, so post-registration
        // mutation is closed by design and a host that wants different
        // options must pre-register them too.
        services.TryAddSingleton<NpgsqlDataSource>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PostgresEventStoreOptions>>().Value;
            return NpgsqlDataSource.Create(opts.ConnectionString);
        });
        services.TryAddSingleton(_ => new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new TenantIdJsonConverter() },
        });

        services.AddSingleton<INpgsqlConnectionFactory, NpgsqlConnectionFactory>();

        // Providers contribute by bounded context. The factory walks every
        // registered IEventTypeProvider once on first resolution; the registry
        // is built and immutable from there. TryAddSingleton so a host can
        // pre-register a fully populated EventTypeRegistry and win. GetServices
        // enumerates at first-resolution time, not registration time, so an
        // IEventTypeProvider may land in the container before or after
        // AddPostgresEventStore.
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

        // PM event types resolve through a separate registry (ADR 0013),
        // populated from IProcessManagerEventTypeProvider exactly as the
        // aggregate registry above is populated from IEventTypeProvider. Stays
        // empty until per-PM-type providers register, which ships with the
        // process managers themselves.
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

        // Command types resolve through CommandTypeRegistry for the delay queue
        // (ADR 0017): a scheduled command is stored by type name and resolved
        // back on dispatch. Populated from ICommandTypeProvider exactly as the
        // event registries above are. Empty until commands that get scheduled
        // register a provider, which ships with the timeout commands.
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

        services.AddSingleton<IEventStore, PostgresEventStore>();

        // Command deduplication store (ADR 0016). Lives with the other Postgres
        // adapters because it consumes the same INpgsqlConnectionFactory; the
        // IdempotencyBehavior that reads it is registered in AddApplication.
        services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();

        // Delay queue (ADR 0017). ScheduleAsync and CancelAsync ship here; the
        // DelayQueueProcessor that drains due rows is a Workers-host background
        // service registered in commit 17.
        services.AddSingleton<IDelayQueue, PostgresDelayQueue>();

        return services;
    }

    // The outbox processor is registered separately from AddPostgresEventStore so a
    // host that writes events but does not drain the outbox composes the event
    // store without it. The Api host dispatches commands over HTTP and lets the
    // Workers host own background processing; if it ran its own OutboxProcessor,
    // that processor would claim outbox rows and dispatch them to its empty handler
    // set (InProcessMessageDispatcher no-ops on zero handlers), mark them processed,
    // and drop the projection and process-manager updates the Workers host should
    // run. Background processors are composed by the host, mirroring
    // AddPostgresDelayQueueProcessor; AddPostgresEventStore stays free of them.
    public static IServiceCollection AddPostgresOutboxProcessor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OutboxProcessorOptions>();
        services.AddSingleton<OutboxRetryPolicy>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OutboxProcessorOptions>>().Value;
            return new OutboxRetryPolicy(opts.BaseSeconds, opts.CapSeconds);
        });
        services.AddSingleton<IMessageDispatcher, InProcessMessageDispatcher>();
        services.AddHostedService<OutboxProcessor>();

        return services;
    }

    // The delay-queue processor is registered separately from AddPostgresEventStore
    // because, unlike the outbox processor, it dispatches through ICausedCommandBus,
    // which AddApplication provides. A host calls this after both
    // AddPostgresEventStore and AddApplication so the bus is resolvable;
    // AddPostgresEventStore stays resolvable on its own for read-side and
    // adapter-only compositions that never start the processor (ADR 0017). The
    // DelayQueueRetryPolicy is a concrete duplicate of OutboxRetryPolicy, not a
    // shared type.
    public static IServiceCollection AddPostgresDelayQueueProcessor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<DelayQueueProcessorOptions>();
        services.AddSingleton<DelayQueueRetryPolicy>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<DelayQueueProcessorOptions>>().Value;
            return new DelayQueueRetryPolicy(opts.BaseSeconds, opts.CapSeconds);
        });
        services.AddHostedService<DelayQueueProcessor>();

        return services;
    }
}
