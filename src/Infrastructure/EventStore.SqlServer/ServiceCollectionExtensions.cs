using System.Text.Encodings.Web;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

public static class ServiceCollectionExtensions
{
    // Mirrors AddPostgresEventStore for what the SQL Server adapter has so far: the store, its
    // two type registries, the options, and the connection factory. The idempotency store and
    // the delay queue are not here, and no host calls this yet. Slice 4 rules how a host chooses
    // between the two adapters; until then this extension exists so the composition it will need
    // is already written and testable.
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

        services.AddSingleton<IEventStore, SqlServerEventStore>();

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
        services.TryAddSingleton<SqlServerOutboxRetryPolicy>();
        services.TryAddSingleton<IMessageDispatcher, InProcessMessageDispatcher>();
        services.AddHostedService<SqlServerOutboxProcessor>();

        return services;
    }

    // snake_case_lower so payload and metadata round-trip through the schema's PERSISTED computed
    // columns, which read JSON_VALUE(metadata, '$.correlation_id') and friends. The options freeze
    // on first use, so a host wanting different ones pre-registers them.
    //
    // Encoder is pinned rather than inherited, matching the PostgreSQL adapter.
    // JavaScriptEncoder.Default escapes every non-ASCII character to \uXXXX, so a serialized
    // payload is pure ASCII by the time it reaches a parameter. That is already the framework
    // default; naming it makes it a decision rather than a default that can be flipped without
    // anyone noticing what it was holding up.
    //
    // It matters more here than on PostgreSQL. Relax it and raw UTF-8 bytes reach the driver, at
    // which point the JSON columns' NVARCHAR binding is the only thing standing between the
    // payload and silent corruption. SqlServerOutboxEncodingTests proves that binding is correct
    // on the raw stored bytes, precisely because the engine-agnostic contract suite cannot: its
    // payloads are ASCII by the time they leave the serializer.
    private static JsonSerializerOptions CreateJsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new TenantIdJsonConverter() },
            Encoder = JavaScriptEncoder.Default,
        };
}
