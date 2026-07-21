using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
        services.TryAddSingleton(_ => CreateJsonOptions());

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

        // The upcaster pipeline reads the same registry the store reads, plus the IEventUpcaster
        // links a host contributes. Empty by default: with no upcasters registered, every event is
        // already at its current version, so the read path is unchanged.
        services.TryAddSingleton(sp => new EventUpcasterPipeline(
            sp.GetRequiredService<EventTypeRegistry>(),
            sp.GetServices<IEventUpcaster>()));

        // The write path stamps events at their current schema version through this port, forwarded to
        // the pipeline so the version written and the version read back come from one topology.
        services.TryAddSingleton<ICurrentEventSchemaVersions>(
            sp => sp.GetRequiredService<EventUpcasterPipeline>());

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

    // The focused head-position read for hosts that compose only the projection-lag
    // read and carry no write stack (the AdminConsole, ADR 0040). Registers the
    // event-store data source, its connection factory, and IEventStoreHeadPosition,
    // and nothing else: no IEventStore, serializer, type registries, outbox, or
    // delay queue. A host that composes the full event store uses AddPostgresEventStore
    // instead, which owns the bare NpgsqlDataSource registration, so the two do not
    // coexist in one host.
    public static IServiceCollection AddEventStoreHeadPosition(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        services.TryAddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<INpgsqlConnectionFactory, NpgsqlConnectionFactory>();
        services.AddSingleton<IEventStoreHeadPosition, PostgresEventStoreHeadReader>();

        return services;
    }

    // The focused correlation-trace read for the AdminConsole Correlation-ID Tracer (Chapter 17).
    // Registers the event-store data source, its connection factory, the JSON options the metadata
    // deserialize needs, and ICorrelationTraceReader. It registers no type registry and no IEventStore:
    // the read resolves no payload type, so it needs neither. Every shared dependency goes in through
    // TryAdd, so this composes beside AddEventStoreHeadPosition and AddEventStoreReplayReader in one
    // host without colliding with either, and stands alone in a host that composes only the tracer.
    public static IServiceCollection AddCorrelationTraceReader(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        services.TryAddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
        services.TryAddSingleton<INpgsqlConnectionFactory, NpgsqlConnectionFactory>();
        services.TryAddSingleton(_ => CreateJsonOptions());
        services.TryAddSingleton<ICorrelationTraceReader, PostgresCorrelationTraceReader>();

        return services;
    }

    // The focused read-side event-store registration for replay: the materialization stack a per-tenant
    // rebuild needs to read and deserialize a tenant's history, plus IEventStore, and nothing else. It
    // registers no NpgsqlDataSource or connection factory (the host holds both from
    // AddEventStoreHeadPosition), no write-side service (no command registry, idempotency store, or delay
    // queue), and no IEventTypeProvider. The providers are concrete bounded contexts, so the composing
    // host supplies them, the same idiom AddPostgresEventStore follows: the registries enumerate the
    // host's IEventTypeProvider registrations to populate.
    public static IServiceCollection AddEventStoreReplayReader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(_ => CreateJsonOptions());

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

        // The write path stamps events at their current schema version through this port, forwarded to
        // the pipeline so the version written and the version read back come from one topology.
        services.TryAddSingleton<ICurrentEventSchemaVersions>(
            sp => sp.GetRequiredService<EventUpcasterPipeline>());

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

        services.AddSingleton<IEventStore, PostgresEventStore>();

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

    /// <summary>
    /// Registers the delay-queue processor against an arbitrary connection string, for a host that
    /// does not call AddPostgresEventStore. A KurrentDB host composes it on its read-model PostgreSQL
    /// database, where the delayed-commands table lives, so its process-manager timeouts still drain
    /// while the event store runs on KurrentDB. It shares the container-owned connection seam the
    /// AddPostgresIdempotencyStore and AddPostgresDelayQueue overloads register.
    /// </summary>
    public static IServiceCollection AddPostgresDelayQueueProcessor(
        this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        // Container-owned connection seam against the read-model database, the same all-TryAdd seam
        // AddPostgresDelayQueue registers, so the processor and the queue it drains share one data
        // source and connection factory.
        services.TryAddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
        services.TryAddSingleton<INpgsqlConnectionFactory, NpgsqlConnectionFactory>();

        // CommandTypeRegistry stays a shared container type (ADR 0026), populated by the same provider
        // walk the event-store and delay-queue registrations use.
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

        // The retry policy and options are the same as the parameterless overload; the difference is
        // only where the connection points and how the serializer shape is supplied.
        services.AddOptions<DelayQueueProcessorOptions>();
        services.AddSingleton<DelayQueueRetryPolicy>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<DelayQueueProcessorOptions>>().Value;
            return new DelayQueueRetryPolicy(opts.BaseSeconds, opts.CapSeconds);
        });

        // The serializer options are captured from this adapter's shape and handed straight to the
        // processor, never resolved from the container: a KurrentDB host registers its own bare
        // JsonSerializerOptions, and a scheduled command must round-trip through the Postgres schema's
        // shape, not that one. Same capture-the-shape discipline as AddPostgresDelayQueue, which is why
        // the processor is built through a factory rather than resolved whole.
        var jsonOptions = CreateJsonOptions();
        services.AddHostedService(sp => new DelayQueueProcessor(
            sp.GetRequiredService<INpgsqlConnectionFactory>(),
            sp.GetRequiredService<CommandTypeRegistry>(),
            jsonOptions,
            sp.GetRequiredService<ICausedCommandBus>(),
            sp.GetRequiredService<DelayQueueRetryPolicy>(),
            sp.GetRequiredService<IOptions<DelayQueueProcessorOptions>>(),
            sp.GetRequiredService<ILogger<DelayQueueProcessor>>()));

        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL idempotency store against an arbitrary connection string, for a host
    /// that does not call AddPostgresEventStore. A KurrentDB host composes it on its read-model
    /// PostgreSQL database, where the command-idempotency table already lives (migrations 0007 and
    /// 0019). Every dependency registers with TryAdd so the call is safe alongside another Postgres
    /// registration; the store gets its own connection path rather than sharing the event store's.
    /// </summary>
    public static IServiceCollection AddPostgresIdempotencyStore(
        this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        // Container-owned data source, on the AddCorrelationTraceReader template: registered through
        // TryAdd so the container disposes it and so a second companion call against the same string
        // reuses it. The idempotency store takes only the connection factory, no serializer.
        services.TryAddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
        services.TryAddSingleton<INpgsqlConnectionFactory, NpgsqlConnectionFactory>();
        services.TryAddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();

        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL delay queue against an arbitrary connection string, for a host that
    /// does not call AddPostgresEventStore. The delayed-commands table lives in the read-model
    /// database (migrations 0008 and 0020). Every dependency registers with TryAdd, and the JSON
    /// serializer options are captured from this adapter's own shape rather than resolved as a bare
    /// container JsonSerializerOptions: a KurrentDB host registers its own bare options, and a
    /// scheduled command must round-trip through the Postgres schema's shape, not that one.
    /// </summary>
    public static IServiceCollection AddPostgresDelayQueue(
        this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        // Same container-owned connection seam as AddPostgresIdempotencyStore, all-TryAdd.
        services.TryAddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
        services.TryAddSingleton<INpgsqlConnectionFactory, NpgsqlConnectionFactory>();

        // CommandTypeRegistry stays a shared container type (ADR 0026), populated by the same
        // provider walk the event-store registration uses.
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

        // The serializer options are captured from this adapter's shape and handed straight to the
        // store, never registered in or resolved from the container. A non-relational host has its
        // own bare JsonSerializerOptions, and a scheduled command must serialize through the Postgres
        // schema's shape, not that one. The capture-the-shape guard fact pins this.
        var jsonOptions = CreateJsonOptions();
        services.TryAddSingleton<IDelayQueue>(sp => new PostgresDelayQueue(
            sp.GetRequiredService<INpgsqlConnectionFactory>(),
            sp.GetRequiredService<CommandTypeRegistry>(),
            jsonOptions));

        return services;
    }

    // The serialization seam is EventStoreJsonOptions.Create() in Infrastructure/Versioning, shared
    // with the other adapters (ADR 0048). This adapter's three registration paths route through this
    // one call, and the shape and the reasoning behind it live with the factory.
    private static JsonSerializerOptions CreateJsonOptions()
        => EventStoreJsonOptions.Create();
}
