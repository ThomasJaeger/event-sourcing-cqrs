using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

public static class ServiceCollectionExtensions
{
    // Mirrors AddPostgresEventStore: the store, its three type registries, the options, the
    // connection factory, and the companion ports (the idempotency store and the delay queue,
    // both registered below). A host that calls this gets a composition complete enough to serve
    // commands, which is the point: the idempotency behavior runs for every command, so an
    // adapter that left IIdempotencyStore unregistered would fail a host at its first one.
    //
    // Two hosts call this: the Api host directly, and the Workers host through
    // WorkersHostFactory. Both reach it from their EVENT_STORE_PROVIDER arm, so which engine
    // holds the events is the configuration choice PLAN.md's Phase 2 provider-switch done-when promises. The registries this
    // populates are the shared ones in Infrastructure/Versioning (ADR 0048), not copies owned
    // here.
    public static IServiceCollection AddSqlServerEventStore(
        this IServiceCollection services,
        Action<SqlServerEventStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.TryAddSingleton(_ => CreateJsonOptions());

        services.TryAddSingleton<ISqlServerConnectionFactory>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SqlServerEventStoreOptions>>().Value;
            return new SqlServerConnectionFactory(opts.ConnectionString);
        });

        // Providers contribute by bounded context, exactly as on the PostgreSQL side. The factory
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

        // The write path stamps events at their current schema version through this port, forwarded to
        // the pipeline so the version written and the version read back come from one topology.
        services.TryAddSingleton<ICurrentEventSchemaVersions>(
            sp => sp.GetRequiredService<EventUpcasterPipeline>());

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

        // Command types resolve through CommandTypeRegistry for the delay queue (ADR 0017): a
        // scheduled command is stored by type name and resolved back on dispatch.
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

        services.AddSingleton<IEventStore, SqlServerEventStore>();

        // The companion ports, bundled exactly as AddPostgresEventStore bundles theirs. They are
        // not IEventStore members: IIdempotencyStore and IDelayQueue are Application-layer
        // dependencies that every complete host composition must supply, and they are database
        // concerns rather than event-store-port concerns. Bundling them here is what lets a host
        // pick a provider and get a complete composition, which is the whole point of the switch
        // that follows.
        services.AddSingleton<IIdempotencyStore, SqlServerIdempotencyStore>();
        services.AddSingleton<IDelayQueue, SqlServerDelayQueue>();

        // The snapshot store companion (Chapter 12), on the same event-store database and the same
        // lifetime, native to this engine per ADR 0004. The snapshotting Order repository resolves it.
        services.AddSingleton<ISnapshotStore, SqlServerSnapshotStore>();

        return services;
    }

    // Split from AddSqlServerEventStore for the same reason the outbox processor is split: a host
    // that composes the store to append or read does not necessarily run the timeout dispatcher,
    // and registering the hosted service unconditionally would start one in every process.
    public static IServiceCollection AddSqlServerDelayQueueProcessor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SqlServerDelayQueueProcessorOptions>();
        services.AddSingleton<SqlServerDelayQueueRetryPolicy>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SqlServerDelayQueueProcessorOptions>>().Value;
            return new SqlServerDelayQueueRetryPolicy(opts.BaseSeconds, opts.CapSeconds);
        });
        services.AddHostedService<SqlServerDelayQueueProcessor>();

        return services;
    }

    // Split from AddSqlServerEventStore for the same reason the PostgreSQL extension splits: a
    // host that composes the store to read or to append does not necessarily want a background
    // service draining the outbox, and registering the hosted service unconditionally would start
    // one in every process that touches the event store.
    public static IServiceCollection AddSqlServerOutboxProcessor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SqlServerOutboxProcessorOptions>();
        // Built from the options, not resolved bare. A bare registration compiles and resolves,
        // because the container fills the policy's constructor from its default parameter values,
        // which are the same 1 and 300 the options default to. So it yields a working policy on
        // the default curve while BaseSeconds and CapSeconds do nothing, and no test that leaves
        // them at their defaults can tell the difference. Mirrors AddPostgresOutboxProcessor.
        services.AddSingleton<SqlServerOutboxRetryPolicy>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SqlServerOutboxProcessorOptions>>().Value;
            return new SqlServerOutboxRetryPolicy(opts.BaseSeconds, opts.CapSeconds);
        });
        services.TryAddSingleton<IMessageDispatcher, InProcessMessageDispatcher>();
        services.AddHostedService<SqlServerOutboxProcessor>();

        return services;
    }

    // The serialization seam is EventStoreJsonOptions.Create() in Infrastructure/Versioning, shared
    // with the other adapters (ADR 0048). The shape and the reasoning behind it live with the
    // factory; the encoder pin matters most on this engine, where the JSON columns' NVARCHAR binding
    // is what stands between a relaxed encoder and silent corruption, and SqlServerOutboxEncodingTests
    // is what proves that binding on the raw stored bytes.
    private static JsonSerializerOptions CreateJsonOptions()
        => EventStoreJsonOptions.Create();
}
